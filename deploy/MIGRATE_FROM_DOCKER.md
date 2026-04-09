# SyncSound migration: Docker -> services

## One-time root setup

```bash
sudo loginctl enable-linger pe6e3
sudo systemctl disable --now nginx || true
cd /home/pe6e3/SyncSound
sudo cp -f deploy/nginx/syncsound.conf /etc/nginx/sites-available/syncsound
sudo ln -sfn /etc/nginx/sites-available/syncsound /etc/nginx/sites-enabled/syncsound
sudo rm -f /etc/nginx/sites-enabled/default /etc/nginx/sites-enabled/vcard
sudo nginx -t
sudo systemctl enable --now nginx
sudo systemctl reload nginx
```

## Stop old Docker stack

```bash
cd /home/pe6e3/SyncSound
docker compose down
```

## Deploy verification

```bash
systemctl --user status syncsound-backend.service --no-pager
curl -I https://антон.su/
curl -I https://антон.su/api/version
```
