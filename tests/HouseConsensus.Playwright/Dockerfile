FROM mcr.microsoft.com/playwright:v1.61.1-noble
WORKDIR /tests
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
ENTRYPOINT ["./scripts/run-e2e.sh"]
