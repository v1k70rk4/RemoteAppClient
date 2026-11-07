"""MkDocs hooks of the website.

What the markdown files cannot do on their own: two pages show files that live elsewhere in the repository,
so each is written once.

* ``changelog.md`` shows ``CHANGELOG.md`` (``<!-- include: CHANGELOG.md -->``).
* ``security/keys.md`` shows ``deploy/KEYS.md`` (``<!-- include: deploy/KEYS.md -->``).
* Files under ``docs/`` that git ignores are not part of the site: a local checkout may keep notes there.

The included file's first-level heading is dropped (the page has its own), and its relative links to other
files of the repository are pointed at GitHub, where those files are.
"""

from __future__ import annotations

import logging
import re
import subprocess
from pathlib import Path, PurePosixPath

log = logging.getLogger("mkdocs.hooks.remoteappclient")

ROOT = Path(__file__).resolve().parent.parent
BLOB = "https://github.com/v1k70rk4/RemoteAppClient/blob/master/"

INCLUDE = re.compile(r"<!--\s*include:\s*([^\s>]+)\s*-->")
LINK = re.compile(r"(\]\()([^)\s#]+)(#[^)\s]*)?(\))")


def _rewrite_links(text: str, base: PurePosixPath) -> str:
    """Relative links of an included file point at GitHub, resolved against the file's own directory."""

    def one(m: re.Match[str]) -> str:
        target = m.group(2)
        if re.match(r"^[a-z]+:", target) or target.startswith("/"):
            return m.group(0)
        resolved = PurePosixPath(*(base / target).parts)
        parts: list[str] = []
        for p in resolved.parts:
            if p == "..":
                if parts:
                    parts.pop()
            elif p != ".":
                parts.append(p)
        return f"{m.group(1)}{BLOB}{'/'.join(parts)}{m.group(3) or ''}{m.group(4)}"

    return LINK.sub(one, text)


def _include(rel: str) -> str:
    path = ROOT / rel
    text = path.read_text(encoding="utf-8")
    lines = text.splitlines()
    if lines and lines[0].startswith("# "):
        lines = lines[1:]
    body = "\n".join(lines).strip("\n") + "\n"
    return _rewrite_links(body, PurePosixPath(rel).parent)


def _ignored_by_git(docs_dir: Path) -> set[str]:
    """The files under docs/ that git ignores, as paths relative to docs/ with forward slashes."""
    try:
        out = subprocess.run(
            ["git", "ls-files", "--others", "--ignored", "--exclude-standard", "--", str(docs_dir)],
            cwd=ROOT, capture_output=True, check=True, text=True, encoding="utf-8",
        ).stdout
    except (OSError, subprocess.CalledProcessError):
        return set()   # no git (a source archive): nothing is ignored
    ignored: set[str] = set()
    for p in out.splitlines():
        if not p:
            continue
        try:
            ignored.add((ROOT / p).resolve().relative_to(docs_dir.resolve()).as_posix())
        except ValueError:
            pass
    return ignored


def on_files(files, config):
    docs_dir = Path(config["docs_dir"])
    ignored = _ignored_by_git(docs_dir)
    for f in list(files):
        if f.src_uri in ignored:
            log.info("%s: ignored by git, not built", f.src_uri)
            files.remove(f)
    return files


def on_page_markdown(markdown: str, page, config, files) -> str:
    def repl(m: re.Match[str]) -> str:
        rel = m.group(1)
        try:
            return _include(rel)
        except OSError as ex:
            log.error("%s: cannot include %s: %s", page.file.src_path, rel, ex)
            raise

    return INCLUDE.sub(repl, markdown)
