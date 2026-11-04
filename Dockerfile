# syntax=docker/dockerfile:1

# ---------------------------------------------------------------------------
# client: webpack builds the browser bundle straight into ../backend/assets
# ---------------------------------------------------------------------------
FROM node:22-alpine AS client
WORKDIR /src/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY frontend/ ./
# webpack output.path is ../backend/assets, i.e. /src/backend/assets here
RUN npm run build

# ---------------------------------------------------------------------------
# runtime: uv installs the backend dependencies from pyproject.toml
# ---------------------------------------------------------------------------
FROM ghcr.io/astral-sh/uv:python3.12-bookworm-slim

ENV PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    UV_COMPILE_BYTECODE=1 \
    UV_LINK_MODE=copy \
    UV_PYTHON_DOWNLOADS=never \
    MECH_DEBUG=0

WORKDIR /app/backend

# dependency layer first, so source edits do not reinstall fastapi/numpy
COPY backend/pyproject.toml ./
RUN --mount=type=cache,target=/root/.cache/uv \
    uv pip install --system -r pyproject.toml

COPY backend/ ./
COPY --from=client /src/backend/assets ./assets

RUN groupadd --gid 10001 mech \
 && useradd --uid 10001 --gid mech --create-home mech \
 && chown -R mech:mech /app
USER mech

EXPOSE 8010
# the uv base image may set an entrypoint of its own - start run.py directly
ENTRYPOINT []

CMD ["python", "run.py", "--host", "0.0.0.0", "--port", "8010"]
