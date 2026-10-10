"""Check every timed output, with byte-equivalence to independently decoded run 1."""
import json, re
import prototype as p

def main():
    rows=json.loads((p.ROOT/'benchmarks/runs.json').read_text(encoding='utf-8')); results=[]; templates={}
    for row in rows:
        path=p.Path(row['output']); idx=p.index(path)
        text,record=p.execute(p.FF,[*p.BASE,*p.GPU,'-i',path,'-map','0','-fps_mode:v','passthrough',
                                    '-progress','pipe:1','-f','null','-'])
        count=int(re.findall(r'^frame=(\d+)',text,re.MULTILINE)[-1])
        if count!=len(idx['Packets']) or 'progress=end' not in text: raise AssertionError('Decoded count incomplete')
        key=(row['seconds'],row['group']); signature=(idx['CodecPrivate'],idx['VideoProperties'],[(x['Pts'],x['Hash']) for x in idx['Packets']])
        if row['iteration']==1: templates[key]=signature
        if key not in templates: raise AssertionError('Missing first-run template')
        # NVENC boundary output was deterministic on this machine, prove rather than assume it.
        same=signature==templates[key]
        if not same: raise AssertionError('Output differs from independently verified first run; needs fresh content comparison')
        audio,_=p.execute(p.PROBE,['-v','error','-select_streams','a','-show_packets','-show_data_hash','sha256',
                                 '-show_entries','packet=stream_index,pts,dts,duration,data_hash','-of','json',path])
        audio=json.loads(audio)
        audio_key=key+('audio',)
        if row['iteration']==1: templates[audio_key]=audio
        if audio!=templates[audio_key]: raise AssertionError('AAC payload/timestamps differ between identical-snapshot runs')
        results.append(dict(output=str(path),frames=count,all_streams_full_decode=True,video_payload_pts_match_run1=True,
                            audio_payload_pts_match_run1=True))
        p.dump(p.ROOT/'benchmarks'/'all-output-integrity.json',dict(passed=True,count=len(results),checks=results))
        print('Verified',row['seconds'],row['group'],row['iteration'],flush=True)

if __name__=='__main__': main()
