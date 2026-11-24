import type {AliasProposal} from './prices.model';

/** "Probably Free Range Eggs · same trip once" / "… same trip 3 times": what the receipts saw, in the words the row shows. */
export function proposalLine(proposal: AliasProposal): string {
  const seen = proposal.trips === 1 ? 'same trip once' : `same trip ${proposal.trips} times`;
  return `Probably ${proposal.productName} · ${seen}`;
}

/** A row's identity: the same words for two products are two rows. */
export function proposalKey(proposal: AliasProposal): string {
  return `${proposal.productId}\u0000${proposal.text.toLowerCase()}`;
}
