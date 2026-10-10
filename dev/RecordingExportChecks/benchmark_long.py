"""Paired production finalizer benchmarks and independent Stage 1 comparison."""
import json,os,statistics,sys
import checks as c

ROOT=c.REPO/'artifacts/unified-stage2'

def benchmark():
    rows=[]
    for seconds in (60,300,600):
        source=c.REPO/'artifacts/unified-stage1/benchmarks'/f'{seconds}-fast'
        for repeat in range(3):
            for mode in (('stage1','fast') if repeat%2==0 else ('fast','stage1')):
                folder=ROOT/'finalizers'/f'{seconds}-r{repeat}-{mode}'
                if not folder.exists():
                    folder.mkdir(parents=True)
                    for file in [*source.glob('video-*.mkv'),*source.glob('*.pcm')]:os.link(file,folder/file.name)
                    (folder/'segments.jsonl').write_bytes((source/'segments.jsonl').read_bytes())
                    manifest=json.loads((source/'recording.json').read_text(encoding='utf-8'))
                    manifest['Settings']['OutputDirectory']=str(folder/'output');manifest['Settings']['BufferDirectory']=str(folder/'buffer')
                    c.dump(folder/'recording.json',manifest)
                result=json.loads((folder/'results.json').read_text(encoding='utf-8')) if (folder/'results.json').exists() else c.finalize(folder,'parallel' if mode=='fast' else mode)
                meta=json.loads(c.Path(result['clip']['Path']).with_suffix('.json').read_text(encoding='utf-8'))
                assert meta['Export']['Strategy']!='legacy-reencode' and meta['Export']['ParallelAudio']==(mode=='fast')
                rows.append(dict(seconds=seconds,repeat=repeat,mode=mode,path=result['clip']['Path'],totalMs=result['totalMs'],export=meta['Export']))
                c.dump(ROOT/'finalizers.json',rows)
                print(seconds,repeat,mode,round(result['totalMs'],2),flush=True)
    summary(rows)

def summary(rows):
    groups={}
    for r in rows:groups.setdefault(str(r['seconds'])+'-'+r['mode'],[]).append(r['totalMs'])
    data={k:dict(n=len(v),medianMs=statistics.median(v),minMs=min(v),maxMs=max(v)) for k,v in groups.items()}
    c.dump(ROOT/'finalizers-summary.json',data);print(json.dumps(data,indent=2),flush=True)

def verify():
    rows=json.loads((ROOT/'finalizers.json').read_text(encoding='utf-8'));checks=[]
    for seconds in (60,300,600):
        for repeat in range(3):
            name=f'{seconds}-r{repeat}';dest=ROOT/'integrity'/(name+'.json')
            if dest.exists():checks.append(json.loads(dest.read_text(encoding='utf-8')));continue
            a,b=[next(r for r in rows if r['seconds']==seconds and r['repeat']==repeat and r['mode']==mode) for mode in ('fast','stage1')]
            av,bv=c.frames(a['path']),c.frames(b['path']);ap,bp=c.packets(a['path']),c.packets(b['path'])
            assert len(av)==len(bv)==30*(seconds-1)
            assert [f[1:5] for f in av]==[f[1:5] for f in bv]
            assert [(p['pts'],p['dts']) for p in ap]==[(p['pts'],p['dts']) for p in bp]
            head=a['export']['EncodedFrames'];assert a['export']['EncodedFrames']==b['export']['EncodedFrames']
            assert [f[-1] for f in av[head:]]==[f[-1] for f in bv[head:]]
            assert [p['data_hash'] for p in ap[head:]]==[p['data_hash'] for p in bp[head:]]
            assert c.frames(a['path'],False)==c.frames(b['path'],False)
            assert c.packets(a['path'],'a')==c.packets(b['path'],'a')
            row=dict(name=name,passed=True,frames=len(av),copied=a['export']['CopiedFrames'],encoded=head,
                all_streams_independently_decoded=True,native_video_pts_dts_equal=True,copied_packets_and_pixels_equal=True,
                all_aac_payloads_samples_pts_padding_equal=True)
            c.dump(dest,row);checks.append(row);c.dump(ROOT/'finalizers-integrity.json',checks)
            print(name,'all-stream integrity passed',flush=True)

if __name__=='__main__':
    if len(sys.argv)>1 and sys.argv[1]=='verify':verify()
    else:benchmark()
