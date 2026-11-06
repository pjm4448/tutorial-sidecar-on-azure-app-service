ARG OLLAMA_IMAGE
FROM ${OLLAMA_IMAGE}

SHELL ["/bin/bash", "-c"]
ENV OLLAMA_MODELS=/models

RUN set -e; ollama serve & PID=$!; \
    trap 'kill "$PID" 2>/dev/null || true' EXIT; \
    READY=0; for i in {1..60}; do \
      if ollama list >/dev/null 2>&1; then READY=1; break; fi; sleep 1; \
    done; test "$READY" = 1; \
    ollama pull phi3:mini; \
    ollama list

ENV OLLAMA_HOST=0.0.0.0:11434 \
    OLLAMA_KEEP_ALIVE=-1 \
    OLLAMA_NUM_PARALLEL=1 \
    OLLAMA_MAX_LOADED_MODELS=1

EXPOSE 11434