FROM node:24-bookworm-slim AS node
FROM golang:1.27-bookworm AS go

FROM ubuntu:24.04

RUN apt-get update \
    && apt-get install --yes --no-install-recommends ca-certificates git libicu74 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=node /usr/local/bin/node /usr/local/bin/node
COPY --from=node /usr/local/lib/node_modules /usr/local/lib/node_modules
COPY --from=go /usr/local/go /usr/local/go
RUN ln -s /usr/local/lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm \
    && ln -s /usr/local/lib/node_modules/npm/bin/npx-cli.js /usr/local/bin/npx

COPY artifacts/self-contained-proof/behaviordiff/ /opt/behaviordiff/
COPY samples/NodeSortDemo/ /fixtures/NodeSortDemo/
COPY samples/GoReference/ /fixtures/GoReference/
COPY tools/verify-self-contained-linux.sh /usr/local/bin/verify-self-contained-linux

ENV PATH=/opt/behaviordiff:/usr/local/go/bin:${PATH}

RUN chmod +x /opt/behaviordiff/behaviordiff \
    /opt/behaviordiff/engines/rust/*/behaviordiff-engine \
    /opt/behaviordiff/tracers/go/*/behaviordiff-go-rewrite \
    /opt/behaviordiff/tracers/rust/*/behaviordiff-rust-rewrite \
    /usr/local/bin/verify-self-contained-linux \
    && test ! -e /usr/share/dotnet \
    && ! command -v dotnet \
    && behaviordiff --help >/dev/null

ENTRYPOINT ["/usr/local/bin/verify-self-contained-linux"]