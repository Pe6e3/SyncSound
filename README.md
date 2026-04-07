# SyncSound

Два сервиса в одном репозитории:

- `client` — Vue 3 + TypeScript (Vite)
- `backend` — ASP.NET 8 Minimal API
- `nginx` — внешний вход и проксирование API

## Что реализовано

- API `GET /api/time` возвращает текущее время сервера в секундах Unix
- Клиент вызывает API каждые 5 секунд
- На главной странице отображается серверное время
- `nginx` отдает клиент и проксирует `/api/*` в backend

## Запуск

Требуется Docker с Compose.

```bash
docker compose up --build -d
```

После старта клиент доступен извне на порту `80`:

- `http://<IP_сервера>/`

Остановка:

```bash
docker compose down
```
