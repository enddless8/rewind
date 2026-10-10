import argparse, dataclasses, json, math
import prototype as p
from verify import validate

def main():
    parser=argparse.ArgumentParser(); parser.add_argument('directory'); args=parser.parse_args()
    directory=p.Path(args.directory).resolve(); rows=[]
    evidence=json.loads((directory/'results.json').read_text(encoding='utf-8'))
    for i in [0,1]:
        root=directory/f'snapshot-{i}'; data=json.loads((root/'snapshot.json').read_text(encoding='utf-8'))
        data['previous_cutoff']=-math.inf if data['previous_cutoff'] is None else data['previous_cutoff']
        data['epoch']=float(data['epoch'])
        data['intervals']=tuple(p.Interval(**{**v,'epoch':float(v['epoch']) if v.get('epoch') is not None else None}) for v in data['intervals']); data['audio']=tuple(data['audio'])
        snapshot=p.Snapshot(**data)
        if snapshot.epoch != float(evidence['AcceptedEpoch']): raise AssertionError('Real accepted epoch not preserved')
        if i==1 and snapshot.history_start != evidence['FirstMetadata']['AcceptedSaveBoundary']: raise AssertionError('Previous acceptance lost')
        p.preindex([r.path for r in snapshot.intervals])
        try: plan=p.make_plan(snapshot)
        except ValueError as e:
            rows.append(dict(snapshot=i,hybrid=False,conservative_rejection=str(e),production_output_playable=True)); continue
        result=p.export(plan,directory/f'hybrid-{i}'); verification=validate(plan,result,directory/f'hybrid-{i}')
        if any(r.source_start+p.index(r.path)['Packets'][piece.first]['Pts']/1000 < max(snapshot.history_start,snapshot.previous_cutoff)-1e-6
               for r,piece in zip(snapshot.intervals,plan.pieces)): raise AssertionError('Old history leakage')
        rows.append(dict(snapshot=i,hybrid=True,result=result,verification=verification,epoch_verified=True,no_old_history=True))
    p.dump(directory/'hybrid-results.json',rows)
    print(json.dumps([dict(snapshot=r['snapshot'],hybrid=r['hybrid'],reason=r.get('conservative_rejection')) for r in rows]),flush=True)

if __name__=='__main__': main()
