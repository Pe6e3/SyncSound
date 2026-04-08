FROM node:22-alpine AS frontend-build
WORKDIR /app

COPY client/package*.json ./
RUN npm install

COPY client/ ./
RUN npm run build

FROM nginx:1.27-alpine
COPY nginx/http-only.conf /etc/nginx/http-only.conf
COPY nginx/ssl.conf.template /etc/nginx/ssl.conf.template
COPY nginx/docker-entrypoint.d/25-syncsound-ssl-config.sh /docker-entrypoint.d/25-syncsound-ssl-config.sh
RUN chmod +x /docker-entrypoint.d/25-syncsound-ssl-config.sh
COPY --from=frontend-build /app/dist /usr/share/nginx/html
