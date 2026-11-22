import {euro, shortDate} from './price-format';
import type {HistoryPoint, PriceHistory} from './price-history.model';

export const LOWEST_TAG = 'Lowest in 90 days';

export interface ChartGeometry {
  /** The step line: the price holds until the next reading. */
  readonly path: string;
  readonly width: number;
  readonly height: number;
  readonly min: number;
  readonly max: number;
}

const WIDTH = 300;
const HEIGHT = 96;
const PAD = 6;
const MS_PER_DAY = 86_400_000;
const BOTH_SIDES = 2;
const HALF = 2;

function dayNumber(day: string): number {
  return Date.parse(`${day}T00:00:00Z`) / MS_PER_DAY;
}

/** The priced readings, oldest first; a reading with no price says nothing about the price and is not drawn. */
export function priced(history: PriceHistory): readonly HistoryPoint[] {
  return history.points.filter(point => point.effective !== null);
}

/**
 * The step-line path over the history's 90 days. The x axis is the calendar (a gap in readings stays a gap), the y axis runs
 * from the lowest to the highest price, so a fake discount shows as a dip that is not below the rest.
 */
export function chart(history: PriceHistory): ChartGeometry | null {
  const points = priced(history);
  if (points.length === 0) {
    return null;
  }
  const prices = points.map(point => point.effective ?? 0);
  const min = Math.min(...prices);
  const max = Math.max(...prices);
  const start = dayNumber(history.from);
  const span = Math.max(1, dayNumber(history.to) - start);
  const x = (day: string): number =>
    PAD + ((dayNumber(day) - start) / span) * (WIDTH - BOTH_SIDES * PAD);
  const y = (price: number): number =>
    max === min
      ? HEIGHT / HALF
      : PAD + (1 - (price - min) / (max - min)) * (HEIGHT - BOTH_SIDES * PAD);
  const commands: string[] = [];
  points.forEach((point, index) => {
    const px = x(point.observedOn).toFixed(1);
    const py = y(point.effective ?? 0).toFixed(1);
    commands.push(index === 0 ? `M${px} ${py}` : `H${px} V${py}`);
  });
  commands.push(`H${(WIDTH - PAD).toFixed(1)}`);
  return {path: commands.join(' '), width: WIDTH, height: HEIGHT, min, max};
}

/** One line of words under the chart: what the price is now against the 90 days. */
export function summary(history: PriceHistory): string {
  const {current, lowest, highest} = history;
  if (current === null || lowest === null || highest === null) {
    return 'No price has been read for this product in this shop in the last 90 days.';
  }
  if (history.lowestIn90Days) {
    return `${euro(current)} is the lowest in 90 days (it was ${euro(highest.price)} on ${shortDate(highest.on)}).`;
  }
  if (lowest.price === highest.price) {
    return `${euro(current)}, unchanged for as long as it has been read.`;
  }
  return `${euro(current)} now. Lowest ${euro(lowest.price)} on ${shortDate(lowest.on)}, highest ${euro(highest.price)} on ${shortDate(highest.on)}.`;
}

export interface ChangeRow {
  readonly day: string;
  readonly price: string;
  readonly note: string;
}

/** Each day the price moved, newest first: the days a price held are not repeated. */
export function changes(history: PriceHistory): readonly ChangeRow[] {
  const rows: ChangeRow[] = [];
  let previous: number | null | undefined;
  for (const point of history.points) {
    if (point.effective !== previous) {
      const note = [
        point.loyaltyScheme && point.loyaltyPrice !== null ? point.loyaltyScheme : '',
        point.promoText ?? '',
      ]
        .filter(Boolean)
        .join(' · ');
      rows.push({
        day: shortDate(point.observedOn),
        price: point.effective === null ? 'No price' : euro(point.effective),
        note,
      });
      previous = point.effective;
    }
  }
  return rows.reverse();
}
