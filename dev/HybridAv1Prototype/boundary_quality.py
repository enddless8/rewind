"""Full-resolution luma/chroma PSNR for encoded boundaries, not only thumbnails."""
import json,math,re
import prototype as p

def main():
    paths=list((p.ROOT/'tests').glob('*/export.json'))+list((p.ROOT/'benchmarks').glob('*-unaligned-hybrid-1/export.json'))
    paths+=list((p.ROOT/'live-final').glob('hybrid-*/export.json'))+[p.ROOT/'concurrent/concurrent-hybrid/export.json',p.ROOT/'final-smoke-30/export.json',p.ROOT/'trial-one-frame/export.json']
    checks=[]
    for path in paths:
        result=json.loads(path.read_text(encoding='utf-8')); data=result['snapshot']
        data['intervals']=tuple(p.Interval(**{**r,'epoch':float(r['epoch']) if r.get('epoch') is not None else None}) for r in data['intervals'])
        data['epoch']=float(data['epoch']);data['previous_cutoff']=float(data['previous_cutoff']);data['audio']=tuple(data['audio'])
        snapshot=p.Snapshot(**data);plan=p.make_plan(snapshot)
        for i,piece in enumerate(plan.pieces):
            frames=piece.boundary_stop-piece.first
            if not frames:continue
            encoded=path.parent/f'boundary-{i}.mkv'; fmt='p010le' if p.probe(encoded)['streams'][0]['pix_fmt']=='yuv420p10le' else 'nv12'
            planar='yuv420p10le' if fmt=='p010le' else 'yuv420p'; stats=path.parent/f'boundary-{i}-full-psnr.txt'
            graph=f'[0:v]hwdownload,format={fmt},format={planar},trim=start_frame={piece.first}:end_frame={piece.boundary_stop},setpts=N/({snapshot.fps}*TB)[source];'
            graph+=f'[1:v]hwdownload,format={fmt},format={planar},setpts=N/({snapshot.fps}*TB)[encoded];[source][encoded]psnr=stats_file={stats.relative_to(p.REPO).as_posix()}[quality]'
            p.execute(p.FF,[*p.BASE,*p.GPU,'-i',piece.path,*p.GPU,'-i',encoded,'-filter_complex',graph,'-map','[quality]','-an','-f','null','-'])
            text=stats.read_text(encoding='utf-8');average=[float(x) for x in re.findall(r'psnr_avg:([0-9.]+|inf)',text)]
            if len(average)!=frames or min(average)<30:raise AssertionError('Full-resolution boundary content mismatch')
            channels={name:min(float(x) for x in re.findall(r'psnr_'+name+r':([0-9.]+|inf)',text)) for name in ['y','u','v']}
            checks.append(dict(output=result['output'],boundary=i,frames=frames,min_psnr_db=min(average),min_channel_psnr=channels,full_resolution=True))
    p.dump(p.ROOT/'boundary-quality.json',dict(passed=True,count=len(checks),frames=sum(r['frames'] for r in checks),checks=checks))
    print('Full-resolution boundary checks:',len(checks),'frames',sum(r['frames'] for r in checks),flush=True)

if __name__=='__main__':main()
