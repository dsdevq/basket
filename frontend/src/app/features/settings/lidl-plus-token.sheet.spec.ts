import {HttpErrorResponse} from '@angular/common/http';
import {TestBed} from '@angular/core/testing';
import {CmnDrawerRef} from '@lifekit-hq/ui';

import {LidlPlusApi} from './lidl-plus.api';
import type {LidlPlusStatus} from './lidl-plus.model';
import {LidlPlusTokenSheetComponent} from './lidl-plus-token.sheet';

const CONNECTED: LidlPlusStatus = {
  state: 'connected',
  connectedAt: '2026-11-05T09:00:00Z',
  verifiedAt: '2026-11-05T09:00:00Z',
};
const TOKEN = 'A1B2C3D4E5F60718293A4B5C6D7E8F90-1';

function render(api: Partial<Record<keyof LidlPlusApi, ReturnType<typeof vi.fn>>>) {
  const ref = {close: vi.fn()};
  TestBed.configureTestingModule({
    providers: [
      {provide: LidlPlusApi, useValue: api},
      {provide: CmnDrawerRef, useValue: ref},
    ],
  });
  const fixture = TestBed.createComponent(LidlPlusTokenSheetComponent);
  fixture.detectChanges();
  const host = fixture.nativeElement as HTMLElement;
  const submit = async (value: string): Promise<void> => {
    const input = host.querySelector('input') as HTMLInputElement;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    host.querySelector('form')?.dispatchEvent(new Event('submit'));
    await new Promise(resolve => setTimeout(resolve, 0));
    fixture.detectChanges();
  };
  return {ref, host, submit};
}

describe('LidlPlusTokenSheetComponent', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('masks the field, so a token is not readable over a shoulder', () => {
    const {host} = render({});

    expect((host.querySelector('input') as HTMLInputElement).type).toBe('password');
  });

  it('sends the trimmed token and closes with the connection it makes', async () => {
    const connect = vi.fn().mockResolvedValue(CONNECTED);
    const {ref, submit} = render({connect});

    await submit(`  ${TOKEN}\n`);

    expect(connect).toHaveBeenCalledWith(TOKEN);
    expect(ref.close).toHaveBeenCalledWith(CONNECTED);
  });

  it('asks for a token before calling the server', async () => {
    const connect = vi.fn();
    const {ref, host, submit} = render({connect});

    await submit('   ');

    expect(connect).not.toHaveBeenCalled();
    expect(ref.close).not.toHaveBeenCalled();
    expect(host.textContent).toContain('Paste the refresh token first.');
  });

  it("stays open and shows the server's reason when Lidl refuses the token, without repeating it", async () => {
    const refusal = new HttpErrorResponse({
      status: 422,
      error: {
        error: 'token_rejected',
        message:
          'Lidl did not accept that token. It may have expired or been used already; get a fresh one.',
      },
    });
    const {ref, host, submit} = render({connect: vi.fn().mockRejectedValue(refusal)});

    await submit(TOKEN);

    expect(ref.close).not.toHaveBeenCalled();
    expect(host.textContent).toContain('Lidl did not accept that token');
    expect(host.textContent).not.toContain(TOKEN);
  });

  it('shows the server reason when Lidl cannot be reached or the token is malformed', async () => {
    for (const [status, message] of [
      [502, 'Could not reach Lidl just now. Nothing was saved; try again in a minute.'],
      [
        400,
        'That does not look like a Lidl Plus refresh token. Paste the whole token, with no spaces.',
      ],
    ] as const) {
      const {host, submit} = render({
        connect: vi.fn().mockRejectedValue(new HttpErrorResponse({status, error: {message}})),
      });
      await submit(TOKEN);
      expect(host.textContent).toContain(message);
      TestBed.resetTestingModule();
    }
  });

  it('says to check the connection when basket itself cannot be reached', async () => {
    const {host, submit} = render({
      connect: vi.fn().mockRejectedValue(new HttpErrorResponse({status: 0})),
    });

    await submit(TOKEN);

    expect(host.textContent).toContain('Could not connect.');
  });
});
