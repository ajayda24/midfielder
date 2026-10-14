# Wire-generated protobuf adapters are referenced reflectively by name.
-keep class dev.midfielder.proto.** { *; }
-keep class com.squareup.wire.** { *; }
# BouncyCastle is only used to create our self-signed certificate.
-dontwarn org.bouncycastle.**
-keep class org.bouncycastle.jcajce.provider.** { *; }
-dontwarn okio.**
