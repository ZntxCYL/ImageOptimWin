# Third-party compressors — version/redistribution information

This unofficial Windows project **does not contain ImageOptim's macOS program code**. It integrates independent executable compressors that are **downloaded by the build script, checksum-verified, and placed beside the final app**. Original authors retain their copyrights; the MIT license for the C# WPF frontend does not replace the third-party licenses.

| Bundled binary/runtime | Pinned version | Windows binary provenance | Upstream project / license | Use in app |
|---|---|---|---|---|
| OxiPNG | 10.2.1 | https://github.com/oxipng/oxipng/releases/tag/v10.2.1 | https://github.com/oxipng/oxipng (MIT) | PNG lossless |
| JPEGOptim | 1.5.6 | https://github.com/tjko/jpegoptim/releases/tag/v1.5.6 | https://github.com/tjko/jpegoptim (GPL-3.0) | JPEG lossless / optional lossy |
| MozJPEG cjpeg, djpeg, jpegtran | 4.1.5 | https://github.com/garyzyg/mozjpeg-windows/releases/tag/4.1.5 (community Windows build) | https://github.com/mozilla/mozjpeg (BSD/IJG/Zlib license families) | jpegtran lossless; cjpeg/djpeg packaged for future MozJPEG operations |
| Gifsicle | 1.97 | https://eternallybored.org/misc/gifsicle/ (Windows port) | https://www.lcdf.org/gifsicle/ (GPL-family; see actual COPYING) | GIF lossless |
| pngquant | 2.18.0 (Windows release) | https://pngquant.org/ | https://pngquant.org/licensing.html (GPL/commercial dual license) | PNG lossy, off by default |
| Node.js | 22.16.0 | https://nodejs.org/dist/v22.16.0/ | https://github.com/nodejs/node (MIT and dependency licenses) | local SVGO runtime, no installation |
| SVGO | 3.3.2 | https://www.npmjs.com/package/svgo | https://github.com/svg/svgo (MIT and dependency licenses) | SVG |

The automated bundler fixes the SHA-256 values of **every downloaded Windows ZIP**. It also includes corresponding source archives for JPEGOptim, pngquant 2.18.0, and Gifsicle 1.97 in `tools/licenses/corresponding-source/`; SVG/npm license files remain in `tools/svgo/node_modules/`. Some engines are not included because they do not have a suitable redistributable Windows package in this implementation (for example PNGOUT, AdvPNG, PNGCrush, Zopfli, Guetzli). Therefore this is an **offline, all-used-engines-included Windows app**, *not* a byte-for-byte port of every algorithm found in the macOS ImageOptim release.

**Important licensing reminder**: For public/commercial redistribution, review the current upstream license texts, GPL obligations (including appropriate corresponding source and notices), and license compatibility/combined-work questions with your distribution plan. Source archives alone are not a blanket compliance guarantee. The build artifact is best treated as an engineering/test build until this review is done. Generated images are not automatically subject to the pngquant GPL license.

Download integrity is checked against the pinned checksums before any downloaded native Windows EXE is accepted. This protects against accidental asset substitution; it is not a code-signing or malware scan substitute. The WPF application itself never downloads binaries when users open or use it.
