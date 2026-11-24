using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Utilities;
using Org.BouncyCastle.Security;
using Renci.SshNet;
using Renci.SshNet.Security;

namespace HerdrRemote.Remote;

// Generate the phone's Ed25519 key pair on first use and keep it in SecureStorage through App.
// The private key never leaves the phone; only the public authorized_keys line is shown for installation on the Mac.
internal static class SshKeyStore
{
	// Store Base64 of the 32-byte private seed followed by the 32-byte public key, as ED25519Key expects ("k || ENC(A)").
	private static async Task<byte[]> GetAsync()
	{
		if (await App.GetSshKeyAsync() is { Length: > 0 } stored) { return Convert.FromBase64String(stored); }
		Ed25519PrivateKeyParameters key = new(new SecureRandom());
		byte[] bytes = [.. key.GetEncoded(), .. key.GeneratePublicKey().GetEncoded()];
		await App.SaveSshKeyAsync(Convert.ToBase64String(bytes));
		return bytes;
	}

	public static async Task<PrivateKeyFile> PrivateKeyAsync() => new(new ED25519Key(await GetAsync()));

	// An authorized_keys line with a device-name comment to distinguish this phone's key.
	public static async Task<string> PublicLineAsync() =>
		$"ssh-ed25519 {Convert.ToBase64String(OpenSshPublicKeyUtilities.EncodePublicKey(new Ed25519PublicKeyParameters(await GetAsync(), 32)))} remoteshell@{DeviceInfo.Current.Name}";
}
