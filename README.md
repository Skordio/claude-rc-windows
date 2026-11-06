# claude-rc-windows

A Windows tray app that keeps Claude Code's Remote Control server
(`claude rc`) running in the folders you choose: pick a folder, flip it ON,
and it starts now, comes back after every restart (at logon, or at boot
before anyone logs on), and is restarted if it exits, until you turn it OFF.

**Status:** in development. The core and the headless host (M1) are done and are driven by
a debug CLI; the tray app is next. See [SPEC.md](SPEC.md) and [docs/PLAN.md](docs/PLAN.md).

Unofficial; not affiliated with or endorsed by Anthropic. Requires Claude
Code installed and signed in for your Windows account.

## License

MIT, see [LICENSE](LICENSE).
