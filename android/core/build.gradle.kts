plugins {
    alias(libs.plugins.kotlin.jvm)
    alias(libs.plugins.wire)
}

java {
    sourceCompatibility = JavaVersion.VERSION_17
    targetCompatibility = JavaVersion.VERSION_17
}

kotlin {
    explicitApi()
    compilerOptions {
        jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17)
        allWarningsAsErrors.set(true)
    }
}

wire {
    sourcePath {
        srcDir("../../protocol")
    }
    kotlin {}
}

dependencies {
    api(libs.kotlinx.coroutines.core)
    api(libs.wire.runtime)
    implementation(libs.bouncycastle.pkix)

    testImplementation(libs.junit.jupiter)
    testImplementation(libs.kotlinx.coroutines.test)
    testRuntimeOnly(libs.junit.platform.launcher)
}

tasks.test {
    useJUnitPlatform()
    // Golden vectors shared with the C# test suite.
    systemProperty("midfielder.vectors", file("../../protocol/test-vectors").absolutePath)
    // Cross-language interop: ./gradlew :core:test -PinteropCli=/path/to/Midfielder.Cli.dll
    providers.gradleProperty("interopCli").orNull?.let { systemProperty("midfielder.interop.cli", it) }
}
