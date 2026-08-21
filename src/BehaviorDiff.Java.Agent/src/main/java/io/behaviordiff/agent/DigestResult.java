package io.behaviordiff.agent;

public final class DigestResult {
    private final String digest;
    private final String rendered;

    DigestResult(String digest, String rendered) {
        this.digest = digest;
        this.rendered = rendered;
    }

    public String digest() {
        return digest;
    }

    public String rendered() {
        return rendered;
    }
}