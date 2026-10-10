import json,os
import checks as c

def case(name,source,fps,height):
    folder=c.fixture('formats/'+name,tracks=('System','Microphone'),start=.75,end=5.75)
    for i,file in enumerate(sorted(folder.glob('video-*.mkv'))):
        file.unlink();os.link(source[i%len(source)],file)
    manifest=json.loads((folder/'recording.json').read_text(encoding='utf-8'))
    manifest['Settings']['Fps']=fps;manifest['Settings']['Height']=height;c.dump(folder/'recording.json',manifest)
    result=c.finalize(folder);out=c.Path(result['clip']['Path']);meta=json.loads(out.with_suffix('.json').read_text(encoding='utf-8'))
    c.check(meta['Export']['Strategy']=='av1-boundary-copy','Real-format boundary+copy '+name,meta['Export'])
    actual=c.frames(out);packets=c.packets(out);expected=[];raw=[]
    for i,file in enumerate(sorted(folder.glob('video-*.mkv'))):
        for p,f in zip(c.packets(file),c.frames(file)):
            if .75<=i*2+int(p['pts'])/1000<5.75:expected.append(p);raw.append(f)
    count=meta['Export']['EncodedFrames']
    c.check(len(actual)==len(expected)==fps*5,'Exact frame count '+name)
    c.check([p['data_hash'] for p in packets[count:]]==[p['data_hash'] for p in expected[count:]],'Exact copied packets '+name)
    c.check([f[-1] for f in actual[count:]]==[f[-1] for f in raw[count:]],'Independent copied frame hashes '+name)
    c.check(all(int(p['dts'])>int(q['dts']) for q,p in zip(packets,packets[1:])),'Native monotonic DTS '+name)
    c.frames(out,False)
    c.dump(folder/'integrity.json',dict(passed=True,frames=len(actual),copied=len(actual)-count,encoded=count))

if __name__=='__main__':
    case('1080p60',sorted((c.REPO/'artifacts/gop-phase2/benchmark/snapshot-120-A-copy-1').glob('owned-*.mkv'))[:3],60,1080)
    case('main10',sorted((c.REPO/'artifacts/hybrid-phase3a/fixtures/ten').glob('part-*.mkv')),30,360)
    c.dump(c.ROOT/'format-results.json',dict(passed=True,count=len(c.CHECKS),checks=c.CHECKS))
