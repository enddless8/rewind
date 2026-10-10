using System.Runtime.InteropServices;
namespace Replay;

internal static class AudioInputTopology
{
    internal record Jack(string PartId, Guid Subtype, uint Channels, int Connection, int Port);
    internal record Node(string Id, string Name, Guid Subtype, int Type);
    internal record Route(List<Jack> Jacks, List<Node> Nodes);
    internal static Route Read(string endpointId)
    {
        var owned = new List<object>();
        T Own<T>(T value) where T : class { owned.Add(value); return value; }
        try
        {
            var enumerator = Own((IEndpoints)Activator.CreateInstance(Type.GetTypeFromCLSID(new("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!);
            Marshal.ThrowExceptionForHR(enumerator.GetDevice(endpointId, out var device)); Own(device);
            var iid = typeof(ITopology).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, 1, 0, out var activated)); Own(activated);
            var topology = (ITopology)activated;
            Marshal.ThrowExceptionForHR(topology.GetConnectorCount(out var count));
            var pending = new Queue<IPart>(); var seen = new HashSet<string>();
            var result = new Route([], []);
            for (uint i=0; i<count && i<16; i++)
            {
                Marshal.ThrowExceptionForHR(topology.GetConnector(i,out var connector)); Own(connector);
                Marshal.ThrowExceptionForHR(connector.GetConnectedTo(out var connected)); Own(connected);
                pending.Enqueue((IPart)connected);
            }
            while (pending.Count > 0)
            {
                var part=pending.Dequeue();
                Marshal.ThrowExceptionForHR(part.GetGlobalId(out var id));
                if (!seen.Add(id)) continue;
                if (seen.Count>128) throw new InvalidOperationException("Слишком сложная топология микрофона.");
                Marshal.ThrowExceptionForHR(part.GetName(out var name));
                Marshal.ThrowExceptionForHR(part.GetSubType(out var subtype));
                Marshal.ThrowExceptionForHR(part.GetPartType(out var type));
                result.Nodes.Add(new(id,name,subtype,type));
                iid=typeof(IJacks).GUID;
                if(part.Activate(1,ref iid,out var jackObject)>=0 && jackObject!=null)
                {
                    Own(jackObject); var jacks=(IJacks)jackObject;
                    Marshal.ThrowExceptionForHR(jacks.GetJackCount(out var jackCount));
                    if(jackCount>64) throw new InvalidOperationException("Некорректная топология микрофона.");
                    for(uint j=0;j<jackCount;j++)
                    { Marshal.ThrowExceptionForHR(jacks.GetJackDescription(j,out var description)); result.Jacks.Add(new(id,subtype,description.Channels,description.Connection,description.Port)); }
                }
                var hr=part.EnumPartsIncoming(out var incoming);
                if(hr==unchecked((int)0x80070490)) continue;
                Marshal.ThrowExceptionForHR(hr); Own(incoming);
                Marshal.ThrowExceptionForHR(incoming.GetCount(out var incomingCount));
                if(incomingCount>128) throw new InvalidOperationException("Некорректная топология микрофона.");
                for(uint j=0;j<incomingCount;j++)
                { Marshal.ThrowExceptionForHR(incoming.GetPart(j,out var next)); Own(next); pending.Enqueue(next); }
            }
            return result;
        }
        finally { for(var i=owned.Count-1;i>=0;i--) Marshal.ReleaseComObject(owned[i]); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct JackDescription
    { public uint Channels, Color; public int Connection, Geo, General, Port; public int Connected; }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEndpoints
    {
        [PreserveSig] int EnumAudioEndpoints(int flow,uint state,out nint collection);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow,int role,out IDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id,out IDevice device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDevice
    { [PreserveSig] int Activate(ref Guid iid,uint context,nint parameters,[MarshalAs(UnmanagedType.IUnknown)] out object value); }
    [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITopology
    { [PreserveSig] int GetConnectorCount(out uint count); [PreserveSig] int GetConnector(uint index,out IConnector connector); }
    [ComImport, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IConnector
    {
        [PreserveSig] int GetConnectorType(out int type);
        [PreserveSig] int GetDataFlow(out int flow);
        [PreserveSig] int ConnectTo(IConnector target);
        [PreserveSig] int Disconnect();
        [PreserveSig] int IsConnected([MarshalAs(UnmanagedType.Bool)] out bool connected);
        [PreserveSig] int GetConnectedTo(out IConnector connected);
    }
    [ComImport, Guid("AE2DE0E4-5BCA-4F2D-AA46-5D13F8FDB3A9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPart
    {
        [PreserveSig] int GetName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int GetLocalId(out uint id);
        [PreserveSig] int GetGlobalId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetPartType(out int type);
        [PreserveSig] int GetSubType(out Guid subtype);
        [PreserveSig] int GetControlInterfaceCount(out uint count);
        [PreserveSig] int GetControlInterface(uint index,out nint value);
        [PreserveSig] int EnumPartsIncoming(out IParts parts);
        [PreserveSig] int EnumPartsOutgoing(out IParts parts);
        [PreserveSig] int GetTopologyObject(out ITopology topology);
        [PreserveSig] int Activate(uint context,ref Guid iid,[MarshalAs(UnmanagedType.IUnknown)] out object value);
    }
    [ComImport, Guid("6DAA848C-5EB0-45CC-AEA5-998A2CDA1FFB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IParts
    { [PreserveSig] int GetCount(out uint count); [PreserveSig] int GetPart(uint index,out IPart part); }
    [ComImport, Guid("4509F757-2D46-4637-8E62-CE7DB944F57B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IJacks
    { [PreserveSig] int GetJackCount(out uint count); [PreserveSig] int GetJackDescription(uint index,out JackDescription description); }
}
