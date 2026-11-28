// A raw string spanning lines is hashed with its line breaks, so this program's code changes if CRLF reaches slangc.

[shader("vertex")]
float4 vertexMain(float2 position : POSITION) : SV_Position
{
    return float4(position, 0.0, 1.0);
}

[shader("fragment")]
float4 fragmentMain() : SV_Target0
{
    uint hash = getStringHash(R"(two
lines)");
    return float4(float(hash & 255u) / 255.0, 0.0, 0.0, 1.0);
}
