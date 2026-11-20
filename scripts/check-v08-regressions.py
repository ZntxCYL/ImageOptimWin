#!/usr/bin/env python3
"""Dependency-free source guards for v0.8 UI, icons, and requeue behavior."""
from pathlib import Path
import re
import struct
import sys
import zlib
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parent.parent
src = root / 'src/ImageOptim.Win'
errors = []


def first_png_pixel(path_or_data):
    """Inspect RGBA PNG dimensions/first pixel using only the stdlib."""
    data = (path_or_data.read_bytes() if isinstance(path_or_data, Path) else path_or_data)
    if data[:8] != b'\x89PNG\r\n\x1a\n':
        raise ValueError('Not a PNG')
    pos = 8
    pieces = []
    width = height = color_type = None
    while pos + 12 <= len(data):
        length = struct.unpack('>I', data[pos:pos + 4])[0]
        tag = data[pos + 4:pos + 8]
        chunk = data[pos + 8:pos + 8 + length]
        if tag == b'IHDR':
            width, height, depth, color_type, comp, filt, interlace = struct.unpack('>IIBBBBB', chunk)
            if depth != 8 or color_type != 6 or interlace != 0:
                raise ValueError('Expected non-interlaced RGBA PNG')
        if tag == b'IDAT':
            pieces.append(chunk)
        pos += 12 + length
        if tag == b'IEND':
            break
    raw = zlib.decompress(b''.join(pieces))
    # First pixel: all PNG scanline filters have a zero left/up neighbor.
    return width, height, tuple(raw[1:5])


def find_ico_png(ico_path, preferred=256):
    data = ico_path.read_bytes()
    if len(data) < 6 or struct.unpack('<HH', data[:4]) != (0, 1):
        raise ValueError('Not a Windows ICO')
    count = struct.unpack('<H', data[4:6])[0]
    for i in range(count):
        info = struct.unpack('<BBBBHHII', data[6 + i*16:6 + (i+1)*16])
        width = info[0] or 256
        height = info[1] or 256
        if width == preferred and height == preferred:
            return data[info[7]:info[7] + info[6]]
    raise ValueError('256px ICO frame missing')

try:
    xaml = (src / 'MainWindow.xaml').read_text(encoding='utf-8')
    code = (src / 'MainWindow.xaml.cs').read_text(encoding='utf-8')
    model = (src / 'Models/ImageJob.cs').read_text(encoding='utf-8')
    csproj = (src / 'ImageOptim.Win.csproj').read_text(encoding='utf-8')
    ET.fromstring(xaml)
    ET.fromstring(csproj)
    for event in ('Window_PreviewDrag', 'Window_PreviewDrop'):
        if event not in xaml or f'void {event}(' not in code:
            errors.append(f'File drag event missing: {event}')
    if 'existing.ResetForRetry();' not in code:
        errors.append('Previously imported paths are not requeued')
    if xaml.count('JobPrimaryText') < 4 or 'JobSecondaryText' not in xaml:
        errors.append('Selection-aware text styles missing')
    if 'RenderTransformOrigin="0.5,0.5"' not in xaml:
        errors.append('Centered spinner rotation missing')
    if 'Assets\\StatusIcons\\*.png' not in csproj:
        errors.append('Status icons are not embedded WPF Resources')
    if '<Resource Include="Assets\\AppIcon.png" />' not in csproj:
        errors.append('Transparent PNG is not embedded as a WPF Resource')
    for window in ('MainWindow.xaml', 'PreferencesWindow.xaml', 'UpdateWindow.xaml'):
        if 'Icon="pack://application:,,,/Assets/AppIcon.png"' not in (src / window).read_text(encoding='utf-8'):
            errors.append(f'{window}: window icon must use transparent PNG')
    for icon in ('wait', 'progress', 'ok', 'noopt', 'err'):
        path = src / f'Assets/StatusIcons/{icon}-display@2x.png'
        if not path.exists():
            errors.append(f'Missing padded status artwork: {path.name}')
        else:
            width, height, corner = first_png_pixel(path)
            if (width, height) != (40, 40) or corner[3] != 0:
                errors.append(f'Padded status artwork malformed: {path.name}')
        if f'{icon}-display@2x.png' not in model:
            errors.append(f'Missing display mapping: {icon}')
    for path in (src / 'Assets/AppIcon.png',):
        width, height, corner = first_png_pixel(path)
        if (width, height) != (512,512) or corner[3] != 0:
            errors.append('Windows app icon still has an opaque green matte')
    ico = find_ico_png(src / 'Assets/AppIcon.ico')
    w, h, pixel = first_png_pixel(ico)
    if (w, h) != (256, 256) or pixel[3] != 0:
        errors.append('Windows ICO has an opaque green matte')
except (ValueError, OSError, ET.ParseError, struct.error, zlib.error, IndexError) as ex:
    errors.append(f'Cannot validate sources: {ex}')

if errors:
    for message in errors:
        print(f'[v0.8 REGRESSION] {message}', file=sys.stderr)
    raise SystemExit(1)
print('[CONFIG OK] v0.8 selection, drag/requeue, status icons, app icon')
