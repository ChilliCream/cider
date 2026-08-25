namespace Cider.AppleContainer.Xpc.Models;

// The images service (com.apple.container.core.container-core-images) and its content-store
// routes carry no additional Codable JSON-blob structs beyond what ContainerConfiguration.cs
// already defines (docs/spikes/xpc/02-apiserver-xpc-protocol.md §6):
//   - imageList / imageTag / imagePull replies and imageSave's request all carry
//     ImageDescription / Descriptor (already modeled — §2.2, §6).
//   - imagePull/imageUnpack/snapshotGet's optional ociPlatform is a Platform (already modeled).
//   - imageLoad's rejectedMembers, imageCleanupOrphanedBlobs' digests, contentDelete/contentClean's
//     digests, and imageDiskUsage's activeImageReferences are all bare `[String]` — no wrapper type
//     needed, just List<string> registered on XpcJsonContext.
//   - contentGet's reply (contentPath), imageDiskUsage's totalCount/activeCount/imageSize/
//     reclaimableSize etc. are plain xpc dictionary values (string/int64/uint64), never JSON blobs —
//     out of scope for wire *models* (transport-level XPC keys belong to X1/X4, not this task).
//
// This file exists to name that scope decision explicitly, per the task's file list, rather than
// leaving it undocumented.
