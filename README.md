# Passload

Passload crée un lien HTTPS temporaire pour télécharger des fichiers directement depuis un PC Windows. Les documents, photos et vidéos restent sur le PC expéditeur. Le destinataire peut prendre un seul fichier ou récupérer toute la sélection en ZIP. Aucun compte, site d’hébergement ou relais de fichiers n’intervient dans le transfert.

**[Télécharger Passload pour Windows x64](https://github.com/mertioz/passload/releases/latest/download/Passload.exe)** · [Versions et empreintes SHA-256](https://github.com/mertioz/passload/releases)

![Fenêtre de Passload permettant de choisir les fichiers, la durée de validité et le réseau](docs/passload.png)

## Envoyer des fichiers

1. Lancez `Passload.exe`. Dans **Envoyer**, déposez vos fichiers ou cliquez sur **Parcourir les fichiers**.
2. Choisissez une durée de 15 minutes, 1 heure, 24 heures ou 7 jours. Utilisez **Internet** pour une personne à distance et **Réseau local** pour un appareil sur votre réseau.
3. Cliquez sur **Créer le lien**. Passload le copie dans le presse-papiers. Envoyez le lien complet à votre destinataire par le moyen de votre choix.
4. Gardez Passload ouvert, le PC éveillé et les fichiers à leur emplacement. Vous pouvez réduire la fenêtre.

Dans **Liens actifs**, **Arrêter le partage** désactive le lien et interrompt les transferts correspondants. L’expiration a le même effet. Fermer l’application désactive tous les liens. Toute personne disposant du lien peut télécharger les fichiers tant qu’il est actif.

## Télécharger un partage

- **Avec Passload sous Windows**, ouvrez **Recevoir**, collez le lien complet et cliquez sur **Afficher les fichiers**. L’application vérifie automatiquement l’empreinte du certificat de l’expéditeur. Choisissez le fichier ou le ZIP, puis son emplacement.
- **Avec un navigateur**, ouvrez le lien pour accéder à la page de téléchargement. Passload crée son certificat HTTPS sur le PC expéditeur. Si le navigateur signale un certificat inconnu, consultez les détails du certificat et comparez son empreinte SHA-256 aux 64 caractères qui suivent `#sha256=` dans le lien reçu de l’expéditeur. Poursuivez uniquement si les empreintes correspondent. Si le navigateur ne permet pas cette vérification, utilisez **Recevoir** sur Windows.

Les téléchargements individuels acceptent les requêtes HTTP de reprise. Passload construit le ZIP pendant l’envoi et conserve les fichiers homonymes en ajoutant un numéro à leur nom dans l’archive.

## Accès depuis Internet

Pour qu’un navigateur situé ailleurs puisse se connecter directement, le PC expéditeur doit accepter une connexion entrante. Le port par défaut est **TCP 47831**.

| Situation | Réglage |
| --- | --- |
| Box avec IPv4 publique et UPnP ou NAT-PMP disponible | Choisissez **Internet**. Passload demande une redirection temporaire à la box, renouvelle son bail pendant le partage et le libère à la fin du dernier partage qui l’utilise. |
| Redirection automatique indisponible | Ouvrez **Connexion**. L’application affiche l’adresse locale du PC et permet d’ouvrir l’interface de la box. Redirigez un port TCP externe vers l’adresse et le port du PC. Dans **Adresse publique manuelle**, indiquez l’IPv4 publique de la box ou votre domaine, avec le port externe si nécessaire, puis enregistrez. |
| IPv6 publique | Autorisez le port TCP dans les pare-feu de Windows et de la box. Le destinataire doit disposer d’un accès IPv6. |
| IPv4 partagée par l’opérateur (CGNAT) ou plusieurs routeurs en cascade | Une redirection sur la seule box peut être insuffisante. Utilisez une IPv6 publique ou une IPv4 publique fournie par l’opérateur. Si vous gérez plusieurs routeurs successifs, configurez la redirection sur chacun. |
| Appareils sur le même réseau local | Choisissez **Réseau local**. La box n’a besoin d’aucune redirection Internet. |

Le bouton **Autoriser dans le pare-feu Windows** demande à Windows d’autoriser l’exécutable sur le port TCP configuré. Cette action nécessite les droits administrateur. Après un déplacement de l’exécutable ou un changement de port, relancez cette action si vous utilisez cette règle.

Un lien Internet créé à partir d’une adresse publique ou d’une redirection doit être essayé depuis un autre réseau, par exemple sur un téléphone en données mobiles. Certaines box traitent différemment les connexions à leur propre adresse publique depuis le Wi-Fi. Les spécifications décrivent le fonctionnement des baux et les plages d’adresses partagées par les opérateurs. [NAT-PMP, RFC 6886](https://www.rfc-editor.org/rfc/rfc6886.html) · [Espace d’adresses partagé, RFC 6598](https://www.rfc-editor.org/rfc/rfc6598.html)

## Compiler et vérifier

Le projet utilise C#, WPF et Kestrel. Sur Windows x64 avec le SDK .NET 10, exécutez ces commandes à la racine du dépôt.

```powershell
dotnet build Passload.slnx -c Release
dotnet run --project tests/Passload.Checks -c Release
dotnet publish src/Passload/Passload.csproj -c Release -r win-x64 -p:PublishProfile=Portable -o artifacts/release
```

Le binaire autonome se trouve dans `artifacts/release/Passload.exe`. Le programme de vérification utilise un serveur HTTPS sur la boucle locale et des passerelles simulées. L’option `--network` teste également l’interface réseau réelle du PC et tente brièvement de partager un fichier de test vide à travers la box.

```powershell
dotnet run --project tests/Passload.Checks -c Release -- --network
```

Les réglages de connexion sont enregistrés dans `%LOCALAPPDATA%\Passload\settings.json`. Les liens et la liste des fichiers partagés restent en mémoire pendant la session.

## Licence

Passload est publié sous [licence MIT](LICENSE). Les licences de Barlow, de Mono.Nat et du runtime .NET distribué sont incluses dans le [dossier des licences](licenses) et accessibles dans **Connexion → Licences du logiciel**.
