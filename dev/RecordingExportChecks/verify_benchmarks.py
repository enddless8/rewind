"""Independent all-stream decoding plus source packet/frame proof for long outputs."""
import json
import checks as c

def main():
    rows=json.loads((c.ROOT/'benchmark.json').read_text(encoding='utf-8'));results=[]
    for seconds in (60,300,600):
        fast=next(r for r in rows if r['seconds']==seconds and r['mode']=='fast')
        legacy=next(r for r in rows if r['seconds']==seconds and r['mode']=='legacy')
        ap,bp=c.packets(fast['output']),c.packets(legacy['output'])
        av,bv=c.frames(fast['output']),c.frames(legacy['output'])
        assert len(av)==len(bv)==(seconds-1)*30
        assert [(r['pts'],r['dts']) for r in ap]==[(r['pts'],r['dts']) for r in bp]
        assert [r[1:5] for r in av]==[r[1:5] for r in bv]
        assert all(int(p['dts'])>int(q['dts']) for q,p in zip(ap,ap[1:]))
        assert c.frames(fast['output'],False)==c.frames(legacy['output'],False)
        folder=c.ROOT/'benchmarks'/f'{seconds}-fast';source=[];source_frames=[];cache={}
        for segment in map(json.loads,(folder/'segments.jsonl').read_text(encoding='utf-8').splitlines()):
            path=folder/segment['Path'];identity=path.stat().st_ino
            if identity not in cache:cache[identity]=(c.packets(path),c.frames(path))
            ps,fs=cache[identity]
            for p,f in zip(ps,fs):
                if 1<=segment['Start']+int(p['pts'])/1000<seconds:source.append(p);source_frames.append(f)
        n=fast['export']['EncodedFrames']
        assert [p['data_hash'] for p in ap[n:]]==[p['data_hash'] for p in source[n:]]
        assert [f[-1] for f in av[n:]]==[f[-1] for f in source_frames[n:]]
        results.append(dict(seconds=seconds,frames=len(av),copied=fast['export']['CopiedFrames'],all_streams_libdav1d_decode=True,
                            exact_payloads=True,exact_copied_decoded_hashes=True,native_pts_dts_equal_legacy=True,aac_samples_hash_pts_equal=True))
        c.dump(c.ROOT/'benchmark-integrity.json',dict(passed=True,checks=results));print(seconds,'integrity passed',flush=True)
if __name__=='__main__':main()
