# Renderer spike (A-9): the official Playwright image, plus playwright-core installed at build time, because the image
# ships browsers but no Playwright package (its build removes /ms-playwright-agent) and `npx playwright run-server`
# would download one from npm at every start. Throwaway; A-10 builds the real image.
FROM mcr.microsoft.com/playwright:v1.64.1-noble@sha256:2f05f8a734509a7b3f26a912639488ee08f58a7b292d14d62271bcff9038768f
WORKDIR /srv/renderer
COPY package.json package-lock.json ./
RUN npm ci --ignore-scripts --no-audit --no-fund && rm -rf /root/.npm
COPY start.sh ./
ENV HOME=/tmp
USER 10002:10002
EXPOSE 3000
ENTRYPOINT ["/bin/sh", "/srv/renderer/start.sh"]
