/**
 * The JSON object a page's script assigns to `marker` (for example `window.__PRELOADED_STATE__=`), cut at its matching
 * brace with a scan that knows strings and escapes, and parsed. The script may carry other statements after the object,
 * so the script text as a whole is not JSON. Undefined when the marker is absent or what follows is not a JSON object.
 * HTML only: nothing is executed.
 */
export function readAssignedJson(html: string, marker: string): Record<string, unknown> | undefined {
  const at = html.indexOf(marker);
  if (at < 0) {
    return undefined;
  }
  const start = at + marker.length + (/^\s*/.exec(html.slice(at + marker.length, at + marker.length + 64))?.[0].length ?? 0);
  if (html[start] !== '{') {
    return undefined;
  }
  let depth = 0;
  let inString = false;
  let escaped = false;
  for (let i = start; i < html.length; i++) {
    const c = html[i]!;
    if (inString) {
      if (escaped) {
        escaped = false;
      } else if (c === '\\') {
        escaped = true;
      } else if (c === '"') {
        inString = false;
      }
    } else if (c === '"') {
      inString = true;
    } else if (c === '{') {
      depth++;
    } else if (c === '}' && --depth === 0) {
      try {
        const value: unknown = JSON.parse(html.slice(start, i + 1));
        return typeof value === 'object' && value !== null && !Array.isArray(value) ? (value as Record<string, unknown>) : undefined;
      } catch {
        return undefined;
      }
    }
  }
  return undefined;
}
