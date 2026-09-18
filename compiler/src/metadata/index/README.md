# Declaration index

Disk-backed exact/prefix lookup and bounded external sorting. The index keeps
all fragments sharing a key, including partial declarations. Readers load only
requested payloads and never materialize the offset directory. Source indexing
and binding integration are separate from this storage layer.
