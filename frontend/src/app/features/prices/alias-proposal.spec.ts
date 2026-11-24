import {proposalKey, proposalLine} from './alias-proposal';
import type {AliasProposal} from './prices.model';

const PROPOSAL: AliasProposal = {
  text: 'Яйця',
  productId: 'p1',
  productName: 'Free Range Eggs',
  trips: 3,
  lastSeen: '2026-11-19',
};

describe('proposalLine', () => {
  it('says how many trips had both', () => {
    expect(proposalLine(PROPOSAL)).toBe('Probably Free Range Eggs · same trip 3 times');
    expect(proposalLine({...PROPOSAL, trips: 2})).toBe(
      'Probably Free Range Eggs · same trip 2 times'
    );
    expect(proposalLine({...PROPOSAL, trips: 1})).toBe('Probably Free Range Eggs · same trip once');
  });
});

describe('proposalKey', () => {
  it('tells one product from another and ignores letter case', () => {
    expect(proposalKey(PROPOSAL)).toBe(proposalKey({...PROPOSAL, text: 'яйця'}));
    expect(proposalKey(PROPOSAL)).not.toBe(proposalKey({...PROPOSAL, productId: 'p2'}));
  });
});
