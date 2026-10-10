"""Long-enough fixtures exercise the production bounded AAC/copy scheduler."""
import json
import checks as c

def main():
    if (c.ROOT/'checks-progress.json').exists():
        c.CHECKS.extend(x for x in json.loads((c.ROOT/'checks-progress.json').read_text(encoding='utf-8')) if x['ok'])
    for name,start,end,tracks,audio_mode in [
        ('aligned',0,60,('System','Microphone','Discord'),'System'),
        ('head',1,60,('System','Microphone','Discord'),'Game'),
        ('frame-after-v2',.033,59.23,('System','Microphone'),'System'),
        ('system-only',0,60,('System',),'System'),
    ]:
        if (c.ROOT/'parallel'/name/'integrity.json').exists():continue
        a=c.fixture('parallel/'+name,seconds=60,start=start,end=end,tracks=tracks)
        b=c.fixture('parallel/'+name+'-stage1',seconds=60,start=start,end=end,tracks=tracks)
        for folder in (a,b):
            m=json.loads((folder/'recording.json').read_text(encoding='utf-8'));m['Settings']['AudioMode']=audio_mode;c.dump(folder/'recording.json',m)
        fast,reference=c.finalize(a,'parallel'),c.finalize(b,'stage1')
        export=json.loads(c.Path(fast['clip']['Path']).with_suffix('.json').read_text(encoding='utf-8'))['Export']
        c.check(export['ParallelAudio'],'Long fixture uses parallel scheduling: '+name)
        c.verify(fast,reference,a)
        c.check(c.packets(fast['clip']['Path'],'a')==c.packets(reference['clip']['Path'],'a'),'Exact AAC payloads/priming/padding: '+name)
        c.check(not list(a.glob('.packet-export-*')),'All intermediates cleaned after success: '+name)
        print(name,'passed',flush=True)
    for mode in ('fail','timeout','audio-fail','mux-fail','audio-corrupt','corrupt','decode-fail','cancel','locked','no-space'):
        folder=c.fixture('parallel/'+mode,seconds=60,end=60,tracks=('System','Microphone'))
        result=c.finalize(folder,'parallel-'+mode)
        if mode in ('locked','no-space','cancel'):
            c.check(not list((folder/'output').glob('*.mkv')),'No publication on '+mode)
        else:
            metadata=json.loads(c.Path(result['clip']['Path']).with_suffix('.json').read_text(encoding='utf-8'))
            c.check(metadata['Export']['Fallback'] if mode!='decode-fail' else metadata['Export']['ParallelAudio'],'Safe fallback/full decode: '+mode)
            c.check(len(c.frames(result['clip']['Path']))==1800,'Real fallback/reference frame count: '+mode)
            c.frames(result['clip']['Path'],False)
        c.check((folder/'System.pcm').exists() and (folder/'video-000000000.mkv').exists(),'Owned inputs retained: '+mode)
        c.check(not list(folder.glob('.packet-export-*')),'All workers joined before cleanup: '+mode)
        print(mode,'passed',flush=True)
    c.dump(c.ROOT/'parallel-results.json',dict(passed=True,count=len(c.CHECKS),checks=c.CHECKS))

if __name__=='__main__':main()
