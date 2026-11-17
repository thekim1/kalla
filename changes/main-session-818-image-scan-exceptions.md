- **The image scan takes dated exceptions** (#818): `scripts/audit/image-scan-exceptions.txt` lists a finding by id,
  image and package with a last day. The first are the six High in the FFmpeg bundled in the parser's
  `opencv-python-headless` wheel: Docling's TableFormer needs `cv2` only to resize images and never reaches FFmpeg, and
  PyPI has no newer 4.x wheel. They apply until 2026-12-17. `audit.py grype` now reads the scan's reports.
