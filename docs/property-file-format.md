# Wreckfest property files (`.tcat`, `.evse`, `.envi`, ...)

Notes for a future track importer that fills the catalogue from the game's own files,
for the base game and for mods: each track's friendly name, its variants, and its
**track length**, which turns a lap count into an estimated race time when building
cups.

Researched on 2026-10-03 against **Wreckfest 1.308438**. Nothing here is used by the
controller yet. Treat offsets as tied to that build, like the RVAs in
`docs/finding-rvas.md`.

## Where the data lives

| Wanted | File | Example (mod 2276098616, Asan Circuit) |
| --- | --- | --- |
| Track name, description | `data/property/event/<track>/<track>.envi` | `Asan Circuit`, "Placed in the remote regions of Japan mountains, ..." |
| Variant name, which scene and track data it uses | `data/property/event/<track>/<variant>.evse` | `Main`, `data/art/tracks/asan/asan_main.scne` |
| Track length, routes, cameras, per-class lap reference times | `data/property/track/<variant>.tcat` | `asan_main.tcat` |

Put together, these give the same "track - variant" names as the built-in catalogue,
for example "Asan Circuit - Main".

**The `.tcat` itself carries no name.** Its only readable string is the free camera's
depth-of-field file. Names come from `.envi` and `.evse`.

**The base game is split.** Its 120 `.tcat` files are loose in
`data/property/track/`, but its `.envi`/`.evse` files are packed into one file,
`data/property/event/events.pckd` (see "Packs" below). Mods ship them loose.

Each name is stored twice: as a localisation key and as English text, in a `tran`
(translation) block, for example `ENVIRONMENT_TITLE_3100601564_12` then `Asan Circuit`.
Store the key as well as the text, as the race results do for car models.

## The container

Every property file starts with a 12-byte header:

| Bytes | Field | Values seen |
| --- | --- | --- |
| 0 | format | `4` LZ4 blocks; `7` LZ4 blocks with an 8-byte block prefix (packs); `1` uncompressed; `8` encrypted (not readable) |
| 4-7 | type tag, **reversed** | `tact` for `tcat`, `esve` for `evse`, `ivne` for `envi`, `dkcp` for `pckd` |
| 8-11 | the type's schema version | `tcat` 20, `evse` 15, `envi` 3, `pckd` 1 |

After the header come LZ4 blocks. For format `4`, each block is a `uint32`
compressed size followed by a raw LZ4 block (no LZ4 frame). For format `7` the prefix
is 8 bytes. Decompressing every block and appending gives the uncompressed file, whose
format byte is then `1`.

This matches [gmazy/bag-decompress](https://github.com/gmazy/bag-decompress) (C, CC0),
which strips the compression, and [Breckfest](https://github.com/MaxxWyndham/Breckfest)'s
`LZ4Decompress.cs`, which it was based on. Neither documents what is inside.

## Inside: the schema's fields, written in order

**The uncompressed body is the type's fields written one after another, in schema
order.** No public document describes it. Bugbear's BagEdit reads it by name, and so
does the game, because both carry the schema.

Seen in `asan_main.tcat` (all little-endian):

| Bytes | Field | Value |
| --- | --- | --- |
| `00 00 00 40` | Minimap Highlight Range (float) | 2.0 |
| `00 00 80 3f` | Minimap Zoom Factor (float) | 1.0 |
| `00 00 f0 41` | pit lane speed limit (km/h, float) | 30.0 |
| `00 00 70 41`, `00 00 40 40` | pit point detection / stopping distance | 15.0, 3.0 |
| `igrg` + `02 00 00 00` | grid generation info: a nested struct (tag `grgi`, version 2), then its fields | |
| `fdxf` + `26 00 00 00` + text | free camera's DOF file: a typed string, length then text | `data/property/effects/dof/default.fxdf` |
| `mact` + `0b 00 00 00` | free cameras: an array, count 11, then each camera | |

So far:

- a plain value is written raw;
- a nested struct is its reversed four-character tag and a version, then its fields;
- a string is its length then the text, with no terminator;
- an array is a count, then its elements.

Track length sits in "Data of the track", after the routes, so reading it means
walking the camera and route arrays first.

## The schema is inside the game

Every `bb*` type's field list is a table in `Wreckfest_x64.exe` (and in
`BagEdit/BagEditCommunity.exe`). The type names are versioned (`bbTrackData12` ...
`bbTrackData16`, plus converters such as `bbTrackData14to15`), and each version has its
own table, so a name string such as "track length" is pointed at from several tables.

To find a table, search the exe for a field name, then for 64-bit pointers to it
(image base `0x140000000`). Each entry is **0x58 bytes**:

| Entry offset | Field |
| --- | --- |
| `+0x00` | type code (`uint32`) |
| `+0x04` | the field's offset in the in-memory struct (`uint32`; `0xFFFFFFFF` for a label) |
| `+0x08` | pointer to the field's display name |
| `+0x10` | pointer to a nested type's descriptor, for nested and array types |
| `+0x18` | further type data, such as `0x0F` for a block |

Type codes seen so far: `2` int, `3` float, `0x15` flags, `0x18` nested block or
array, `0x1B` label (a caption BagEdit shows, not stored), `0x1C` unknown.

One table, `bbTrackData` "Data of the track", read from the dedicated server's exe:

| Struct offset | Type | Field |
| --- | --- | --- |
| `0xB0` | `0x15` | flags |
| `0xB4` | 3 float | track length |
| `0xB8` | `0x18` | surface infos |
| `0xD0`, `0xD4`, `0xD8` | 2 int | checkpoint race start time (ms), classes A, B, C |
| `0xDC`, `0xE0`, `0xE4` | 2 int | checkpoint race first span optimal time (ms), classes A, B, C |
| `0xE8` | `0x18` | time gate infos |

Struct offsets describe memory, not the file. In the file only the order matters.

## Packs

`data/property/event/events.pckd` holds the base game's events: format `7`, tag
`pckd`, then entries tagged `pckf`, named relative to `data/property/event/`
(`bigstadium/....envi`, `..._demolition_arena.evse`). Its entry layout has not been
worked out.

## A decoder to start from

Enough to decompress format `4` and look inside, written during the research:

```python
import struct

def lz4_block(src):
    out = bytearray()
    i = 0
    while i < len(src):
        token = src[i]; i += 1
        literals = token >> 4
        if literals == 15:
            while True:
                x = src[i]; i += 1; literals += x
                if x != 255:
                    break
        out += src[i:i + literals]; i += literals
        if i >= len(src):
            break
        offset = src[i] | src[i + 1] << 8; i += 2
        length = token & 15
        if length == 15:
            while True:
                x = src[i]; i += 1; length += x
                if x != 255:
                    break
        for _ in range(length + 4):
            out.append(out[-offset])
    return bytes(out)

def read_property_file(path):
    data = open(path, 'rb').read()
    assert data[0] == 4, 'only format 4 here'
    tag = data[4:8][::-1].decode()
    body = bytearray()
    pos = 12
    while pos < len(data):
        size = struct.unpack_from('<I', data, pos)[0]; pos += 4
        body += lz4_block(data[pos:pos + size]); pos += size
    return tag, bytes(body)
```

## Open questions

- Type code `0x1C`, and the full set of codes.
- How an array's element type is described in a schema entry.
- The `events.pckd` entry layout.
- Whether `.envi` and `.evse` hold anything else worth importing, such as the game
  modes a variant allows.

## Sources

- [Track Data (*.tcat), Unofficial Wreckfest Wiki](https://tads.me.uk/wfwiki/index.php?title=Files_Explained%3ATCAT): what a `.tcat` contains, with field limits
- [Modding:Tools, Unofficial Wreckfest Wiki](https://tads.me.uk/wfwiki/index.php?title=Modding:Tools)
- [gmazy/bag-decompress](https://github.com/gmazy/bag-decompress)
- [MaxxWyndham/Breckfest](https://github.com/MaxxWyndham/Breckfest)
- [Wreckfest Modding Tools (mazay.fi)](https://mazay.fi/wf/)
