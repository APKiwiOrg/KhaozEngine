# Metal frame submission

## Attachment store intent

`IGpuCommandList.SetFramebufferWithStoreActions` accepts `GpuAttachmentStoreActions`. Zero means Store for
every attachment. `DiscardColourMask` chooses individual colour attachments. `DiscardDepth` and
`DiscardStencil` are independent, including on a combined-format texture. Intent applies to every native
rendering pass opened for that binding. The caller must not reopen a pass and load discarded data after an
intervening transfer or dispatch. A backend may conservatively retain discarded contents.

Metal maps dead attachments to `MTLStoreAction.DontCare`. Vulkan maps them to `AttachmentStoreOp.DontCare`.
D3D11 retains them through its ordinary framebuffer bind because its existing pipeline has no per-plane
store control. Its store-intent entry point retains the recording guard and the caller identifier. There is no live GL backend. The retired OpenGL selection does not construct a device. No GL call
or backend is introduced by this intent.

The directional cascade pass owns one R32F colour atlas and separate D32FloatS8UInt working storage. Every
cascade draws into that atlas in one uninterrupted render encoder. Matrix and skinning uploads use uniform
ring CPU writes, so they add no transfer boundary inside it. The pass clears the whole depth and stencil
before its first draw. Sampling, shadow captures, readback and dirty-skip reuse name only the R32F colour
atlas. The working depth/stencil texture is retained only by the graph, framebuffer and matching pipelines,
then disposed with that graph. Later cascades share the same encoder. Every next-frame rebuild clears it
again, and a skipped frame never reads it.

This resolves #595's determinism concern. Discarded working bytes are undefined, but no output depends on
them before their next clear. The colour atlas and every attachment with a later reader still use Store.
Bitwise GPU comparisons cover the sampled atlas and all six main attachments after three scene frames.
