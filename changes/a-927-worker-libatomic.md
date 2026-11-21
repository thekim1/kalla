- **JavaScript rendering works again** (#927): since 0.9.0 every render failed with `render.unavailable`, because
  Playwright 1.65.1's Node driver links `libatomic.so.1` and the worker's chiseled image had none (the worker logged
  `error while loading shared libraries: libatomic.so.1`). The worker image now carries Ubuntu noble's
  `libatomic1` (`14.2.0-4ubuntu2~24.04.1`, SHA-256-pinned) next to the driver, and CI and the release start the driver
  inside the built image.
