namespace Unity.Collections
{
    public struct NativeArray<T> where T : struct
    {
        public int Length => 0;
        public void CopyTo(T[] array) { }
    }
}
