import { mkdir, writeFile } from 'node:fs/promises';
import { dirname } from 'node:path';

/** Writes one JSON report to a file (parents made) or, with no path, to stdout. Never anything but the report itself. */
export async function writeReport(report: unknown, path: string | undefined, stdout: (text: string) => void = (text) => process.stdout.write(text)): Promise<void> {
  const text = `${JSON.stringify(report, null, 2)}\n`;
  if (!path) {
    stdout(text);
    return;
  }
  await mkdir(dirname(path), { recursive: true });
  await writeFile(path, text, 'utf8');
}
