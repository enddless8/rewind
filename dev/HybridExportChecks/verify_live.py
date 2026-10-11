"""Independent post-benchmark media evidence, never part of measured latency."""
import json, sys
from pathlib import Path
from checks import ROOT,REPO,dump
sys.path.insert(0,str(REPO/'dev/HybridAv1Prototype'))
import prototype as p
from verify import frame_rows

def main():
    root=ROOT/(sys.argv[1] if len(sys.argv)>1 else 'live-matrix-v1');evidence=[]
    for folder in sorted(root.glob('*-*-*')):
        if (folder/'completed-attempt.json').exists():folder=Path(json.loads((folder/'completed-attempt.json').read_text())['folder'])
        result=folder/'results.json'
        if not result.exists():continue
        data=json.loads(result.read_text(encoding='utf-8'));output=Path(data['clip']['Path'])
        if (folder/'independent-integrity.json').exists():
            evidence.append(json.loads((folder/'independent-integrity.json').read_text(encoding='utf-8')));continue
        text,_=p.execute(p.LAB,[*p.BASE,'-c:v','libdav1d','-i',output,'-map','0','-f','framemd5','-'])
        rows=frame_rows(text);video=[r for r in rows if r[0]=='0'];metadata=data['metadata']
        expected=int(__import__('math').ceil(float(f"{data['clip']['Duration']:.6f}")*60-1e-9))
        if len(video)!=expected:raise AssertionError('Legacy-equivalent frame count failed')
        probe,_=p.execute(p.PROBE,['-v','error','-show_packets','-show_entries','packet=stream_index,pts,dts','-of','json',output]);packets=json.loads(probe)['packets']
        last={};n=0
        for packet in packets:
            stream=packet['stream_index'];pts=packet['pts'];dts=packet['dts']
            if stream in last and (pts<=last[stream][0] or dts<=last[stream][1]):raise AssertionError('Nonmonotonic PTS/DTS')
            if stream==0 and (pts!=dts or pts!=round(n*1000/60)):raise AssertionError('Video packet grid differs')
            if stream==0:n+=1
            last[stream]=(pts,dts)
        if not data['CaptureRunning'] or metadata['ActualEnd']>metadata['AcceptedSaveBoundary'] or metadata['PreviousSaveCutoff'] is not None and metadata['ActualStart']<metadata['PreviousSaveCutoff']:raise AssertionError('History/capture invariant failed')
        report=dict(passed=True,folder=str(folder),decoder=metadata['ExportDecoder'],frames=len(video),streams=len(last),
            full_independent_all_stream_decode=True,strict_packet_pts_dts=True,history_bounds=True,capture_running=True,
            encoded=metadata['EncodedFrames'],copied=metadata['CopiedFrames'],audio=[dict(stream=stream,decoded_frames=len(v),samples=sum(int(r[3]) for r in v),first_pts=v[0][2],last_pts=v[-1][2])
                for stream in sorted({r[0] for r in rows if r[0]!='0'}) if (v:=[r for r in rows if r[0]==stream])])
        dump(folder/'independent-integrity.json',report);evidence.append(report)
        print(folder.name,metadata['ExportDecoder'],len(video),'independent full decode passed',flush=True)
    dump(root/'integrity.json',dict(passed=True,outputs=len(evidence),video_frames=sum(e['frames'] for e in evidence),evidence=evidence))
if __name__=='__main__':main()
