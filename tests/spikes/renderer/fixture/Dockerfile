# Fixture sites for the renderer spike; the repo's pinned Python base (src/parser/Dockerfile).
FROM python:3.12.15-slim-trixie@sha256:a0f57064cbdcd6285fa45f51237ab0b35ea8ebaed06a1f5973e89e19dfb86704
COPY server.py /srv/fixture/server.py
COPY www /srv/fixture/www
USER 10003:10003
ENTRYPOINT ["python", "-I", "/srv/fixture/server.py"]
