import {euro, shortDate} from '../prices/price-format';
import type {CatalogItem} from './catalog.model';

/** What a result row shows on the right: the best price today, else what the shop says instead. Empty when no price was ever read. */
export function catalogPrice(item: CatalogItem): string {
  const price = item.price;
  if (!price) {
    return '';
  }
  switch (price.state) {
    case 'current':
      return price.inStock ? euro(price.best) : 'Out of stock';
    case 'upcoming':
      return `from ${shortDate(price.validFrom)}`;
    case 'ended':
      return 'ended';
    default:
      return 'no price';
  }
}

/** The line under the name: the brand (when the name does not already say it), the size, and the shop's own price per unit. */
export function catalogDetail(item: CatalogItem): string {
  const brand = item.brand?.trim();
  const parts: string[] = [];
  if (brand && !item.name.toLowerCase().includes(brand.toLowerCase())) {
    parts.push(brand);
  }
  if (item.sizeText) {
    parts.push(item.sizeText);
  }
  if (item.unitPrice !== null && item.unitPriceBasis) {
    parts.push(`${euro(item.unitPrice)} / ${item.unitPriceBasis}`);
  }
  return parts.join(' · ');
}
