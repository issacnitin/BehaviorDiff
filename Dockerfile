# syntax=docker/dockerfile:1.7

ARG DOTNET_IMAGE=mcr.microsoft.com/dotnet/sdk:8.0-noble
ARG RUNTIME_IMAGE=ubuntu:24.04
ARG JAVA_IMAGE=maven:3.9.11-eclipse-temurin-17
ARG NODE_IMAGE=node:24-bookworm-slim
ARG GO_IMAGE=golang:1.27-bookworm
ARG RUST_IMAGE=rust:1.98-bookworm

FROM ${JAVA_IMAGE} AS java-build
WORKDIR /source
COPY src/RealDiff.Java.Agent/pom.xml .
RUN mvn --batch-mode --no-transfer-progress dependency:go-offline
COPY src/RealDiff.Java.Agent/ .
RUN mvn --batch-mode --no-transfer-progress package -DskipTests \
    && mkdir -p /out \
    && find target -maxdepth 1 -type f -name 'realdiff-java-agent-*.jar' ! -name 'original-*' \
        -exec cp '{}' /out/realdiff-java-agent.jar \;

FROM ${NODE_IMAGE} AS node-build
WORKDIR /out
COPY src/RealDiff.Node/package.json src/RealDiff.Node/package-lock.json ./
RUN npm ci --omit=dev --ignore-scripts --no-audit --no-fund
COPY src/RealDiff.Node/register.cjs src/RealDiff.Node/loader.mjs src/RealDiff.Node/bootstrap.mjs ./
COPY src/RealDiff.Node/src/ ./src/
COPY src/RealDiff.Node/adapters/ ./adapters/
RUN rm -rf node_modules/.bin

FROM ${GO_IMAGE} AS go-build
ARG TARGETARCH
WORKDIR /source
COPY src/RealDiff.Go/go.mod ./
RUN go mod download
COPY src/RealDiff.Go/ .
RUN case "$TARGETARCH" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; *) exit 1 ;; esac \
    && mkdir -p "/out/$rid" \
    && CGO_ENABLED=0 go build -trimpath -ldflags='-s -w' -o "/out/$rid/realdiff-go-rewrite" ./cmd/realdiff-go-rewrite

FROM ${RUST_IMAGE} AS rust-build
ARG TARGETARCH
WORKDIR /source
COPY src/RealDiff.Engine.Rust/Cargo.toml src/RealDiff.Engine.Rust/Cargo.lock ./
RUN mkdir src && printf 'fn main() {}\n' > src/main.rs && cargo build --release --locked && rm -rf src
COPY src/RealDiff.Engine.Rust/src/ ./src/
RUN touch src/main.rs \
    && cargo build --release --locked \
    && case "$TARGETARCH" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; *) exit 1 ;; esac \
    && mkdir -p "/out/$rid" \
    && cp target/release/realdiff-engine "/out/$rid/realdiff-engine"

FROM ${RUST_IMAGE} AS rust-launcher-build
WORKDIR /source
COPY src/RealDiff.Launcher.Rust/Cargo.toml src/RealDiff.Launcher.Rust/Cargo.lock ./
COPY src/RealDiff.Launcher.Rust/src/ ./src/
RUN cargo build --release --locked \
    && cp target/release/realdiff /out-realdiff

FROM ${RUST_IMAGE} AS rust-tracer-build
ARG TARGETARCH
WORKDIR /source
COPY src/RealDiff.Rust.Tracer/Cargo.toml src/RealDiff.Rust.Tracer/Cargo.lock ./
COPY src/RealDiff.Rust.Tracer/runtime/ ./runtime/
COPY src/RealDiff.Rust.Tracer/src/ ./src/
RUN cargo build --release --locked \
    && case "$TARGETARCH" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; *) exit 1 ;; esac \
    && mkdir -p "/out/$rid" \
    && cp target/release/realdiff-rust-rewrite "/out/$rid/realdiff-rust-rewrite"

FROM ${DOTNET_IMAGE} AS dotnet-build
ARG TARGETARCH
WORKDIR /source
COPY global.json Directory.Build.props README.md ./
COPY src/RealDiff.Contracts/ src/RealDiff.Contracts/
COPY src/RealDiff.Tracer/ src/RealDiff.Tracer/
COPY src/RealDiff.Tracer.Xunit/ src/RealDiff.Tracer.Xunit/
COPY src/RealDiff.Cli/ src/RealDiff.Cli/
COPY tools/Weaver/ tools/Weaver/
RUN case "$TARGETARCH" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; *) exit 1 ;; esac \
    && dotnet publish src/RealDiff.Cli/RealDiff.Cli.csproj \
        --configuration Release --runtime "$rid" --self-contained true --output /out --nologo \
        --source https://www.nuget.org/api/v2/ \
        -p:NuGetAudit=false -p:SelfContainedRelease=true -p:PublishTrimmed=false \
    && mv /out/realdiff /out/realdiff-managed

FROM ${RUNTIME_IMAGE} AS runtime
ARG BUILD_VERSION=dev
LABEL org.opencontainers.image.title="RealDiff" \
    org.opencontainers.image.description="RealDiff CLI, engine, and .NET, Java, Node, Go, Rust, and Python tracing toolchains" \
      org.opencontainers.image.source="https://github.com/issacnitin/RealDiff" \
      org.opencontainers.image.version="${BUILD_VERSION}" \
      org.opencontainers.image.licenses="MIT"

RUN apt-get update \
    && apt-get install --yes --no-install-recommends build-essential ca-certificates git libicu74 python3.12 python3-pytest \
    && rm -rf /var/lib/apt/lists/*

COPY --from=dotnet-build /usr/share/dotnet /usr/share/dotnet
COPY --from=java-build /opt/java/openjdk /opt/java/openjdk
COPY --from=java-build /usr/share/maven /usr/share/maven
COPY --from=node-build /usr/local/bin/node /usr/local/bin/node
COPY --from=node-build /usr/local/lib/node_modules /usr/local/lib/node_modules
COPY --from=go-build /usr/local/go /usr/local/go
COPY --from=rust-tracer-build /usr/local/cargo /usr/local/cargo
COPY --from=rust-tracer-build /usr/local/rustup /usr/local/rustup

RUN ln -s /usr/share/maven/bin/mvn /usr/local/bin/mvn \
    && ln -s /usr/local/lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm \
    && ln -s /usr/local/lib/node_modules/npm/bin/npx-cli.js /usr/local/bin/npx \
    && ln -s /usr/share/dotnet/dotnet /usr/local/bin/dotnet \
    && ln -s /opt/realdiff/tracers/go/linux-$(uname -m | sed 's/x86_64/x64/;s/aarch64/arm64/')/realdiff-go-rewrite \
        /usr/local/bin/realdiff-go-rewrite

COPY --from=dotnet-build /out/ /opt/realdiff/
COPY --from=rust-launcher-build /out-realdiff /opt/realdiff/realdiff
COPY --from=java-build /out/realdiff-java-agent.jar /opt/realdiff/tracers/java/realdiff-java-agent.jar
COPY --from=node-build /out/ /opt/realdiff/tracers/node/
COPY --from=go-build /out/ /opt/realdiff/tracers/go/
COPY --from=rust-build /out/ /opt/realdiff/engines/rust/
COPY --from=rust-tracer-build /out/ /opt/realdiff/tracers/rust/
COPY src/RealDiff.Python/sitecustomize.py /opt/realdiff/tracers/python/sitecustomize.py
COPY src/RealDiff.Python/realdiff_python/ /opt/realdiff/tracers/python/realdiff_python/
COPY docker/realdiff.sh /usr/local/bin/realdiff
COPY docker/action-entrypoint.sh /usr/local/bin/realdiff-action
COPY docker/entrypoint.sh /usr/local/bin/realdiff-entrypoint

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    JAVA_HOME=/opt/java/openjdk \
    CARGO_HOME=/usr/local/cargo \
    RUSTUP_HOME=/usr/local/rustup \
    REALDIFF_JAVA_AGENT=/opt/realdiff/tracers/java/realdiff-java-agent.jar \
    REALDIFF_NODE_TRACER=/opt/realdiff/tracers/node \
    REALDIFF_PYTHON=/usr/bin/python3.12 \
    REALDIFF_PYTHON_TRACER=/opt/realdiff/tracers/python \
    DOTNET_ROOT=/usr/share/dotnet \
    PATH=/opt/java/openjdk/bin:/usr/local/go/bin:/usr/local/cargo/bin:/usr/share/dotnet:${PATH}

RUN chmod +x /usr/local/bin/realdiff /usr/local/bin/realdiff-action \
    /usr/local/bin/realdiff-entrypoint \
    /opt/realdiff/tracers/go/*/realdiff-go-rewrite \
    /opt/realdiff/engines/rust/*/realdiff-engine \
    /opt/realdiff/tracers/rust/*/realdiff-rust-rewrite \
    && dotnet --info >/dev/null \
    && java -version \
    && mvn --version \
    && node --version \
    && npm --version \
    && go version \
    && cargo --version \
    && rustc --version \
    && python3.12 --version \
    && python3.12 -m pytest --version

WORKDIR /workspace
ENTRYPOINT ["/usr/local/bin/realdiff-entrypoint"]
