import type {ScorecardResponse, ScorecardStore} from './scorecard.model';

/** Test helpers: a scorecard with every field set, overridden per case. */
export function entry(over: Partial<ScorecardStore> = {}): ScorecardStore {
  return {store: 'lidl', storeName: 'Lidl', wins: 0, share: 0, ...over};
}

export function card(over: Partial<ScorecardResponse> = {}): ScorecardResponse {
  return {
    asOf: '2026-11-14',
    staleAfterDays: 3,
    months: 6,
    trips: 0,
    compared: 0,
    notCompared: 0,
    saved: 0,
    topStore: null,
    stores: [
      entry({store: 'lidl', storeName: 'Lidl'}),
      entry({store: 'aldi', storeName: 'Aldi'}),
      entry({store: 'tesco', storeName: 'Tesco'}),
      entry({store: 'dunnes', storeName: 'Dunnes'}),
    ],
    trend: [],
    ...over,
  };
}
