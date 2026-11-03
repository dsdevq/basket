export interface Person {
  readonly id: string;
  readonly name: string;
}

export interface ListItem {
  readonly id: string;
  readonly text: string;
  readonly checked: boolean;
  readonly addedAt: string;
  readonly addedBy: Person;
  readonly checkedAt: string | null;
  readonly checkedBy: Person | null;
}
