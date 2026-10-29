"""Lenient JSON reader for Vintage Story asset files.

Game JSON is parsed by Newtonsoft in lenient mode: keys may be unquoted,
strings may use single quotes, comments and trailing commas are allowed.
Strict json.loads is tried first because it is much faster; this reader
is the fallback. Later duplicate keys replace earlier ones, as in
Newtonsoft's default.
"""
import json
import re

_TOKEN = re.compile(r'''
  (?P<ws>(?:\s+|//[^\n]*|/\*.*?\*/)+)
 |(?P<str>"(?:[^"\\]|\\.)*"|'(?:[^'\\]|\\.)*')
 |(?P<num>[+-]?(?:0[xX][0-9a-fA-F]+|(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?))
 |(?P<punct>[{}\[\]:,])
 |(?P<ident>[A-Za-z_$][\w$\-.]*)
''', re.S | re.X)


class VsJsonError(ValueError):
    pass


def _tokens(text):
    pos, n = 0, len(text)
    out = []
    while pos < n:
        m = _TOKEN.match(text, pos)
        if not m:
            raise VsJsonError("bad character %r at offset %d" % (text[pos], pos))
        pos = m.end()
        kind = m.lastgroup
        if kind == "ws":
            continue
        out.append((kind, m.group(kind)))
    return out


def _string(raw):
    if raw[0] == '"':
        return json.loads(raw)
    body = raw[1:-1].replace('\\\'', '\'').replace('"', '\\"')
    return json.loads('"' + body + '"')


def _number(raw):
    if raw.lower().startswith(("0x", "+0x", "-0x")):
        return int(raw, 16)
    v = float(raw)
    if re.fullmatch(r'[+-]?\d+', raw):
        return int(raw)
    return v


class _Parser:
    def __init__(self, toks):
        self.t = toks
        self.i = 0

    def peek(self):
        return self.t[self.i] if self.i < len(self.t) else (None, None)

    def take(self):
        tok = self.peek()
        self.i += 1
        return tok

    def value(self):
        kind, raw = self.take()
        if kind == "punct" and raw == "{":
            return self.obj()
        if kind == "punct" and raw == "[":
            return self.arr()
        if kind == "str":
            return _string(raw)
        if kind == "num":
            return _number(raw)
        if kind == "ident":
            if raw == "true":
                return True
            if raw == "false":
                return False
            if raw == "null":
                return None
            if raw in ("Infinity", "NaN"):
                return float(raw.lower() if raw == "NaN" else "inf")
            return raw  # bare word, keep as string
        raise VsJsonError("unexpected token %r" % (raw,))

    def obj(self):
        out = {}
        while True:
            kind, raw = self.peek()
            if kind == "punct" and raw == "}":
                self.take()
                return out
            if kind == "punct" and raw == ",":
                self.take()
                continue
            kind, raw = self.take()
            if kind == "str":
                key = _string(raw)
            elif kind in ("ident", "num"):
                key = raw
            else:
                raise VsJsonError("bad key token %r" % (raw,))
            k2, r2 = self.take()
            if not (k2 == "punct" and r2 == ":"):
                raise VsJsonError("expected ':' after key %r" % key)
            out[key] = self.value()

    def arr(self):
        out = []
        while True:
            kind, raw = self.peek()
            if kind == "punct" and raw == "]":
                self.take()
                return out
            if kind == "punct" and raw == ",":
                self.take()
                continue
            out.append(self.value())


def loads(text):
    if text.startswith("﻿"):
        text = text[1:]
    try:
        return json.loads(text)
    except ValueError:
        pass
    p = _Parser(_tokens(text))
    return p.value()


def load(path):
    with open(path, "r", encoding="utf-8-sig") as f:
        return loads(f.read())
