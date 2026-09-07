#!/usr/bin/env bash
# Clones and builds the Dosai corpus test corpus at pinned commits.
# Run once before executing CorpusTests; see README.md for what each app covers.
set -euo pipefail

CORPUS_ROOT="${HOME}/sandbox/dosai-corpus"
mkdir -p "${CORPUS_ROOT}"

clone_at_commit() {
    local repo_url="$1"
    local commit="$2"
    local target_dir="$3"

    if [ -d "${target_dir}/.git" ]; then
        echo "Already cloned: ${target_dir}"
        return
    fi

    git clone --quiet "${repo_url}" "${target_dir}"
    git -C "${target_dir}" checkout --quiet "${commit}"
}

echo "==> eShopOnWeb (ASP.NET Core MVC + minimal APIs + auth + configuration)"
clone_at_commit \
    https://github.com/dotnet-architecture/eShopOnWeb \
    4da8212117e87d808d4bbc7da6286fd2147ce606 \
    "${CORPUS_ROOT}/eShopOnWeb"
dotnet build "${CORPUS_ROOT}/eShopOnWeb/eShopOnWeb.sln" -v quiet

echo "==> practical-aspnetcore orleans-1 (Microsoft Orleans minimal hosting)"
clone_at_commit \
    https://github.com/dodyg/practical-aspnetcore \
    91fb02ba0ea97266c58c04fd0cadfdaa1b899022 \
    "${CORPUS_ROOT}/practical-aspnetcore"
dotnet build "${CORPUS_ROOT}/practical-aspnetcore/projects/orleans/orleans-1" -v quiet

echo "==> Corpus ready at ${CORPUS_ROOT}"
