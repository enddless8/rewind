"""Real Matroska timestamp corruption, plus recording gap-prefix behavior."""
import json
import checks as c

def vint(data,pos,identifier=False):
    first=data[pos];n=1
    while not first&(1<<(8-n)):n+=1
    value=first if identifier else first&((1<<(8-n))-1)
    for b in data[pos+1:pos+n]:value=value*256+b
    return value,n
def blocks(data,start,end):
    pos=start
    while pos<end:
        identifier,n=vint(data,pos,True);pos+=n;size,m=vint(data,pos);pos+=m
        stop=end if size==(1<<(7*m))-1 else pos+size
        if identifier in (0x18538067,0x1f43b675):yield from blocks(data,pos,stop)
        elif identifier==0xa3:yield pos
        pos=stop
def main():
    folder=c.fixture('timestamps/nonmonotonic')
    target=folder/'video-000000000.mkv';data=bytearray(target.read_bytes());items=list(blocks(data,0,len(data)))
    _,n=vint(data,items[1]);data[items[1]+n:items[1]+n+2]=b'\0\0'
    target.unlink();target.write_bytes(data)
    text,*_=c.call(c.DOTNET,[c.HARNESS,'plan',folder]);decision=json.loads(text)
    c.check(not decision['eligible'],'Nonmonotonic actual MKV timestamps reject packet reuse',decision)
    folder=c.fixture('timestamps/gap-prefix',gap=True)
    result=c.finalize(folder);metadata=json.loads(c.Path(result['clip']['Path']).with_suffix('.json').read_text(encoding='utf-8'))
    c.check(result['clip']['Duration']==2 and metadata['ActualEnd']==2,'Recording stops at the original confirmed contiguous prefix across a recovery gap')
    c.check(len(c.frames(result['clip']['Path']))==60,'Gap prefix fully decodes exactly 60 frames')
    c.frames(result['clip']['Path'],False)
    folder=c.fixture('timestamps/fractional-concat',end=2.0339)
    source=folder/'video-000000000.mkv';source.unlink()
    c.call(c.FF,['-v','error','-i',c.SOURCE/'part-000.mkv','-map','0:v:0','-c:v','copy','-frames:v','59','-y',source])
    segments=[dict(Path='video-000000000.mkv',Start=0,End=59/30),dict(Path='video-000000001.mkv',Start=59/30,End=59/30+2)]
    (folder/'segments.jsonl').write_text(''.join(json.dumps(s)+'\n' for s in segments),encoding='utf-8')
    text,*_=c.call(c.DOTNET,[c.HARNESS,'plan',folder]);decision=json.loads(text)
    c.check(not decision['eligible'],'Fractional concat double-rounding cannot select an unintended tail frame',decision)
    c.dump(c.ROOT/'timestamp-results.json',dict(passed=True,count=len(c.CHECKS),checks=c.CHECKS))
if __name__=='__main__':main()
