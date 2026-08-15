using OpenEleven.Server;
using OpenEleven.Server.Configuration;

// Pes2010Pc: one process serves exactly one title. The profile selects which
// profile-gated commands register and which profile assembly is scanned.
return await OpenElevenServerHost.Run(GameProfile.Pes2010Pc, args);