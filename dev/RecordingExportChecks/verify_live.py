"""Compare the actual accepted Stop snapshot against its legacy finalizer."""
import json,os
import checks as c

def main():
    root=c.ROOT/'live-eligible';result=json.loads((root/'results.json').read_text(encoding='utf-8'))
    source=root/'retained';legacy=root/'legacy';legacy.mkdir(exist_ok=False)
    for file in source.iterdir():
        if file.suffix=='.mkv':os.link(file,legacy/file.name)
        else:(legacy/file.name).write_bytes(file.read_bytes())
    manifest=json.loads((legacy/'recording.json').read_text(encoding='utf-8'))
    manifest['Settings']['OutputDirectory']=str(legacy/'output');c.dump(legacy/'recording.json',manifest)
    reference=c.finalize(legacy,'legacy');a=result['result']['Path'];b=reference['clip']['Path'];actual,expected=c.frames(a),c.frames(b)
    assert len(actual)==len(expected)==902
    assert [r[1:5] for r in actual]==[r[1:5] for r in expected]
    assert c.frames(a,False)==c.frames(b,False)
    ap,bp=c.packets(a),c.packets(b)
    assert [(p['pts'],p['dts']) for p in ap]==[(p['pts'],p['dts']) for p in bp]
    selected=[];raw=[];first=None;cursor=0
    begin=float(f"{result['metadata']['ActualStart']-next(map(json.loads,(source/'segments.jsonl').read_text(encoding='utf-8').splitlines()))['Start']:.6f}")
    duration=float(f"{result['result']['Duration']:.6f}")
    for segment in map(json.loads,(source/'segments.jsonl').read_text(encoding='utf-8').splitlines()):
        path=source/segment['Path']
        for p,f in zip(c.packets(path),c.frames(path)):
            if begin<=cursor+int(p['pts'])/1000<begin+duration:selected.append(p);raw.append(f)
        cursor+=float(f"{segment['End']-segment['Start']:.6f}")
    encoded=result['metadata']['Export']['EncodedFrames']
    assert len(selected)==len(actual)
    assert [p['data_hash'] for p in ap[encoded:]]==[p['data_hash'] for p in selected[encoded:]]
    assert [f[-1] for f in actual[encoded:]]==[f[-1] for f in raw[encoded:]]
    for clip in [result['replay']['Path'],result['replayDuringStop']['Path']]:c.frames(clip);c.frames(clip,False)
    queue=json.loads((root/'compression-queue.json').read_text(encoding='utf-8'));job=queue[0]
    assert job['State']=='Completed' and c.Path(a).exists()
    compressed=job['Output'];c.frames(compressed);c.frames(compressed,False)
    assert [p['data_hash'] for p in c.packets(a,'a')]==[p['data_hash'] for p in c.packets(compressed,'a')]
    c.dump(root/'integrity.json',dict(passed=True,frames=len(actual),copied=len(actual)-encoded,encoded=encoded,
        source_packet_hashes_exact=True,source_frame_hashes_exact=True,native_pts_dts_equal=True,
        aac_samples_pts_hash_equal=True,replay_full_decode=True,mp4_all_streams_full_decode=True,mp4_aac_payloads_preserved=True,
        legacy_same_snapshot_total_ms=reference['totalMs']))
    print('Live production MKV/source/reference/concurrent replay/MP4 integrity passed')
if __name__=='__main__':main()
