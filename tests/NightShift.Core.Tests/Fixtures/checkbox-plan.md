# Sample Project — Master Plan

A checkbox plan shaped like the ones NightShift actually runs against: prose that wraps at
about ninety-five columns, block quotes carrying amendments, a table of choices, fenced blocks
that document the plan's own conventions, and task items in all three marks.

---

## 1. Conventions

This section documents the format, which means it shows examples **inside a fence**. Nothing in
here is real work, and nothing in here may be counted:

```markdown
- [ ] an item that is still open
- [x] an item that is finished
- [!] an item that is blocked
### M1 — an example milestone heading
```

Outside a fence the same text is real. Inline code such as `<Nullable>enable</Nullable>` and
`--token-limit <n|max>` must survive intact, because every angle bracket in this file lives
inside a span like those.

## 2. Choices

| Concern | Choice | Why |
|:---|:---:|---:|
| UI framework | Avalonia | the only mature cross-platform desktop stack for .NET |
| Serialization | `System.Text.Json` | source-generated, and already in the box |
| Installer | Velopack | one tool for all four runtime identifiers |

> **Refinement (2026-07-27): the table above is settled.**
>
> Two of the three were re-checked after the first release, and neither moved:
>
> - Avalonia 12 shipped in April and the upgrade was mechanical.
> - Velopack's macOS packaging needed `--mainExe`, which is a flag, not a rethink.

## 3. Work

- [x] Scaffold the solution and get CI green on three operating systems
- [x] Read `~/.claude.json` and locate the project's trust keys
- [ ] Wire the *usage gauge* to the real provider rather than the stub
- [ ] Add a **plain-language** explanation to every preflight row
- [!] Clean-machine install on a fresh Windows box — needs the machine and a person
- [ ] Nested work:
  - [ ] the first half
  - [x] the second half, which is done

1. First ordered step
2. Second ordered step
3. Third ordered step

***

Escapes matter too: a literal \*asterisk\* and a literal \_underscore\_ stay as typed. Access-key
notation like the _window item and the _style item must not pair up into emphasis. Strikethrough
reads as ~~removed~~ and a link reads as [the docs](https://example.com/docs).
