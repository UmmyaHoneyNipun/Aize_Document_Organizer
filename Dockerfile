FROM node:23-alpine AS frontend-build
WORKDIR /src/web

COPY src/web/pid-portal/package.json ./
RUN npm install

COPY src/web/pid-portal/ ./
ARG VITE_DOCUMENT_API_BASE_URL=
ENV VITE_DOCUMENT_API_BASE_URL=${VITE_DOCUMENT_API_BASE_URL}
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS dotnet-build
WORKDIR /src

COPY src/dotnet ./src/dotnet
RUN dotnet publish ./src/dotnet/services/DocumentService/Aize.DocumentService.Api/Aize.DocumentService.Api.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 python3-pip python3-venv \
    && rm -rf /var/lib/apt/lists/*

COPY src/python/document_processor/requirements.txt /tmp/python-requirements.txt
RUN python3 -m venv /opt/pyenv \
    && /opt/pyenv/bin/pip install --no-cache-dir -r /tmp/python-requirements.txt

COPY --from=dotnet-build /app/publish ./
COPY --from=frontend-build /src/web/dist ./wwwroot
COPY src/python/document_processor/app ./python/app
COPY deploy/docker/start-all-in-one.sh /app/start-all-in-one.sh

RUN chmod +x /app/start-all-in-one.sh \
    && mkdir -p /app/data/blob-storage

ENV ASPNETCORE_URLS=http://+:8080
ENV OpenApi__Enabled=true
ENV PythonProcessor__BaseUrl=http://127.0.0.1:8001
ENV DOCUMENT_API_BASE_URL=http://127.0.0.1:8080
ENV LocalBlobStorage__RootPath=/app/data/blob-storage
ENV PYTHONPATH=/app/python
ENV PATH=/opt/pyenv/bin:$PATH

EXPOSE 8080
EXPOSE 8001

ENTRYPOINT ["/app/start-all-in-one.sh"]
