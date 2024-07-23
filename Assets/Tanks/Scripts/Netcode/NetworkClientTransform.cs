using Unity.Netcode.Components;

namespace Netcode.Examples.Tanks
{
    public class NetworkClientTransform: NetworkTransform
    {
        protected override bool OnIsServerAuthoritative() => false;
    }
}