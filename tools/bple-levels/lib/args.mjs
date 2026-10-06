// Shared CLI argument helpers. The tools/bple-* tools all follow the same
// `--name <value>` / `--flag` convention, so both CLIs read their options through here.

/// The value of `--<name> <value>`, or `fallback` when the flag is absent or has no value.
export function arg(name, fallback) {
  const index = process.argv.indexOf(`--${name}`);
  return index >= 0 && process.argv[index + 1] ? process.argv[index + 1] : fallback;
}

/// True when `--<name>` is present (a flag, not a valued option).
export function flag(name) {
  return process.argv.includes(`--${name}`);
}
