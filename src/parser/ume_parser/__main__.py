"""`python -m ume_parser`: one uvicorn process (no `--workers`, no `--limit-concurrency`; parser design §10.1)."""

import logging

import uvicorn

from ume_parser.app import app
from ume_parser.settings import Settings


def main() -> None:
    settings = Settings()
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")
    uvicorn.run(
        app,
        host=settings.host,
        port=settings.port,
        ssl_certfile=settings.tls_cert_file,
        ssl_keyfile=settings.tls_key_file,
        proxy_headers=False,
        server_header=False,
    )


if __name__ == "__main__":
    main()
