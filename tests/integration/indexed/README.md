# Indexed compilation fixtures

Compile consumers using a disk declaration index, not --ref implementation
sources. Namespace, alias and fully qualified references must load only Value;
the unused declaration would fail binding if imported eagerly. Separate corlink
execution must produce the expected result with normal stack maps enabled.
This initial gate does not certify generic/partial ownership or managed layouts.
