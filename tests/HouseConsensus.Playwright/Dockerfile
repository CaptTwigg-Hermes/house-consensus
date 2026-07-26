FROM mcr.microsoft.com/playwright:v1.61.1-noble
WORKDIR /tests
COPY tests/HouseConsensus.Playwright/package.json tests/HouseConsensus.Playwright/package-lock.json ./
RUN npm ci
COPY tests/HouseConsensus.Playwright/ .
COPY src/Client/wwwroot/css/app.css /src/Client/wwwroot/css/app.css
ENTRYPOINT ["./scripts/run-e2e.sh"]
