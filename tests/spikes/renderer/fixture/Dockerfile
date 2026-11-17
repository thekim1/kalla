# Fixture sites for the renderer spike; the repo's pinned Python base (src/parser/Dockerfile).
FROM python:3.12.16-slim-trixie@sha256:5e2bdb1b0d542d5039cffb8181c65930093949194a65c365dfb1b0fe13a4fa5b
COPY server.py /srv/fixture/server.py
COPY www /srv/fixture/www
USER 10003:10003
ENTRYPOINT ["python", "-I", "/srv/fixture/server.py"]
