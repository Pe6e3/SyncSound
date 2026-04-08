#!/usr/bin/env bash
# Первичная выдача сертификата Let's Encrypt (certbot, webroot) без остановки nginx.
# На сервере: скопируйте .env.example в .env, задайте DOMAIN и EMAIL, откройте порты 80 и 443 наружу.
set -euo pipefail

cd "$(dirname "$0")/.."

if [[ ! -f .env ]]; then
  echo "Создайте файл .env из .env.example и задайте DOMAIN и EMAIL." >&2
  exit 1
fi

# shellcheck disable=SC1091
set -a
source ./.env
set +a

if [[ -z "${DOMAIN:-}" || "${DOMAIN}" == "localhost" ]]; then
  echo "В .env укажите реальный публичный DOMAIN (не localhost)." >&2
  exit 1
fi

if [[ -z "${EMAIL:-}" ]]; then
  echo "В .env укажите EMAIL для регистрации в Let's Encrypt." >&2
  exit 1
fi

STAGING_ARG=()
if [[ "${STAGING:-0}" == "1" ]]; then
  STAGING_ARG=(--staging)
  echo "Режим staging ACME (тестовые сертификаты)."
fi

RSA_KEY_SIZE="${RSA_KEY_SIZE:-4096}"
mkdir -p certbot/conf certbot/www certbot/var

echo "Сборка и запуск nginx (временный самоподписанный сертификат, чтобы контейнер поднялся с TLS)…"
docker compose build nginx
docker compose run --rm --entrypoint /bin/sh certbot -c "mkdir -p '/etc/letsencrypt/live/${DOMAIN}' && openssl req -x509 -nodes -newkey rsa:${RSA_KEY_SIZE} -days 1 \
  -keyout '/etc/letsencrypt/live/${DOMAIN}/privkey.pem' \
  -out '/etc/letsencrypt/live/${DOMAIN}/fullchain.pem' \
  -subj \"/CN=${DOMAIN}\""

docker compose up -d

echo "Удаление временного сертификата перед запросом у Let's Encrypt…"
docker compose run --rm --entrypoint /bin/sh certbot -c "\
  rm -rf '/etc/letsencrypt/live/${DOMAIN}' '/etc/letsencrypt/archive/${DOMAIN}' \
  && rm -f '/etc/letsencrypt/renewal/${DOMAIN}.conf'"

echo "Запрос сертификата certbot (webroot)…"
docker compose run --rm --entrypoint certbot certbot certonly \
  --webroot -w /var/www/certbot \
  --email "${EMAIL}" --agree-tos --no-eff-email \
  "${STAGING_ARG[@]}" \
  -d "${DOMAIN}"

echo "Перезапуск nginx с настоящим сертификатом…"
docker compose restart nginx

echo "Готово. Откройте https://${DOMAIN}/ — микрофон для калибровки будет доступен в безопасном контексте."
