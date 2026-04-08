#!/bin/sh
# Если для $DOMAIN уже есть сертификат Let's Encrypt — включаем HTTPS-конфиг, иначе остаёмся на HTTP (удобно для первого деплоя и локальной разработки).
set -e
DOMAIN_VALUE="${DOMAIN:-localhost}"
export DOMAIN="$DOMAIN_VALUE"
if [ -f "/etc/letsencrypt/live/$DOMAIN_VALUE/fullchain.pem" ]; then
    envsubst '$DOMAIN' < /etc/nginx/ssl.conf.template > /etc/nginx/conf.d/default.conf
else
    cp /etc/nginx/http-only.conf /etc/nginx/conf.d/default.conf
fi
