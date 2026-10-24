# Contributing to ChameLab

Bug reports, ideas and pull requests are welcome. Open an
[issue](https://github.com/m0n5ter/ChameLab/issues) first for anything bigger than a fix,
so we can agree on the approach before you spend time on it.

## Pull requests

- One change per PR: a fix, a feature or a cleanup, not all three.
- Commit messages say what changed for the user, with the area first:
  `Slots: match the slot's card to Saved cards by UID`.
- Add a line to the `[unreleased]` section of [CHANGELOG.md](CHANGELOG.md) for anything a
  user would notice. Start it with `ChameLab:` when it is about the app.
- Test on a real device where you can, and say in the PR what you tested on.

## The app

The sources are in [software/chamelab](software/chamelab), with build steps in its
[README](software/chamelab/README.md).

- Match the code around your change: naming, comment density, ReactiveUI patterns.
- Use `var` for local variables wherever it compiles.
- UI text is in American English: color, behavior, canceled.
- Keep it working on every target: desktop, Android, iOS and the browser. Anything
  platform-specific goes behind the platform's own project or transport.

## The firmware

- New commands get ids in ChameLab's own ranges (for example, 1900 for slot colors), so
  they never collide with commands of other firmware.
- Add the command to the CLI as well, so it can be used without the app.
- Bump [`firmware/VERSION`](firmware/VERSION) only when the firmware itself changes.

## The CLI

- Use [uv](https://docs.astral.sh/uv/). New dependencies go into `pyproject.toml` and
  `uv.lock`.
- The code is type-checked with [pyrefly](https://pyrefly.org/) and linted with
  [ruff](https://docs.astral.sh/ruff/). CI runs both; run them before opening a PR:

  ```bash
  cd software
  uv run ruff check .
  uv run pyrefly check
  ```

- Don't add a package for something that takes a few lines.
