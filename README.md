# Passload

Partagez un ou plusieurs fichiers depuis Windows, Linux ou macOS avec un lien temporaire. Le destinataire ouvre le lien dans son navigateur et télécharge un fichier ou toute la sélection en ZIP. L’appareil expéditeur garde les originaux.

## Téléchargements

### Windows

**[Installer Passload pour Windows x64](https://github.com/mertioz/passload/releases/latest/download/Passload-Setup.exe)**

Cet installateur ajoute Passload au menu Démarrer et à **Applications installées**. L’[exécutable portable](https://github.com/mertioz/passload/releases/latest/download/Passload.exe) se lance directement depuis son dossier.

### Linux

| Format | x64 | ARM64 |
| --- | --- | --- |
| Debian et Ubuntu | [.deb](https://github.com/mertioz/passload/releases/latest/download/passload-linux-x64.deb) | [.deb](https://github.com/mertioz/passload/releases/latest/download/passload-linux-arm64.deb) |
| Fedora et distributions RPM | [.rpm](https://github.com/mertioz/passload/releases/latest/download/passload-linux-x64.rpm) | [.rpm](https://github.com/mertioz/passload/releases/latest/download/passload-linux-arm64.rpm) |
| Archive portable | [.tar.gz](https://github.com/mertioz/passload/releases/latest/download/passload-linux-x64.tar.gz) | [.tar.gz](https://github.com/mertioz/passload/releases/latest/download/passload-linux-arm64.tar.gz) |

### macOS

| Format | Intel x64 | Apple Silicon ARM64 |
| --- | --- | --- |
| Installateur | [.pkg](https://github.com/mertioz/passload/releases/latest/download/passload-osx-x64.pkg) | [.pkg](https://github.com/mertioz/passload/releases/latest/download/passload-osx-arm64.pkg) |
| Archive portable | [.tar.gz](https://github.com/mertioz/passload/releases/latest/download/passload-osx-x64.tar.gz) | [.tar.gz](https://github.com/mertioz/passload/releases/latest/download/passload-osx-arm64.tar.gz) |

[Toutes les versions et leurs fichiers](https://github.com/mertioz/passload/releases)

![Fenêtre principale de Passload](docs/passload.png)

Passload est disponible en français, anglais, allemand, espagnol, japonais et chinois simplifié. Sous Windows, choisissez une langue dans **Paramètres → Langue**. Les pages de réception suivent la langue du navigateur.

## Envoyer sous Windows

`Passload-Setup.exe` ajoute Passload au menu Démarrer et à **Applications installées**. `Passload.exe` peut être lancé directement depuis son dossier.

1. Lancez `Passload.exe`. Déposez les fichiers dans la fenêtre ou choisissez **Parcourir les fichiers**.
2. Choisissez **Réseau public** pour Internet ou **Réseau privé** pour le même réseau, puis la durée. Vous pouvez renseigner **Nom et description** avant de cliquer sur **Créer le lien**. Envoyez l’adresse complète à votre destinataire.
3. Gardez le PC allumé et connecté, et les fichiers à leur emplacement. Vous pouvez fermer la fenêtre de Passload.

**Liens → Ouvrir ici** ouvre un aperçu sur le PC expéditeur. **Modifier le lien** permet de changer le titre et la description que le destinataire voit sur la page. L’adresse reste utilisable. **Arrêter le partage** interrompt les transferts et désactive le lien. L’expiration met aussi fin au partage.

Les éditions GitHub vérifient les versions au lancement et toutes les heures. Le bouton **Vérifier les mises à jour** se trouve dans **Connexion**. Passload vérifie l’empreinte SHA-256 du fichier publié et attend la fin des opérations en cours avant de remplacer l’exécutable. Le Microsoft Store gère les mises à jour de son édition MSIX.

### Partager en arrière-plan

Fermer la fenêtre range Passload près de l’horloge tant qu’un partage ou un transfert est en cours. Chaque lien garde sa propre expiration. Quand le dernier lien et le dernier transfert sont terminés, Passload se ferme automatiquement si la fenêtre est masquée. Double-cliquez sur l’icône ou relancez l’exécutable pour retrouver la même session. **Quitter Passload**, dans le menu de l’icône, arrête tous les liens.

Passload empêche la veille automatique de Windows pendant un partage actif. Une mise en veille volontaire ou une coupure du réseau interrompt l’accès pendant cette période. Les liens cessent de fonctionner lorsque le PC s’éteint. Si le port préféré est occupé, un partage automatique peut utiliser un autre port libre.

### Suivre les téléchargements

Dans **Liens**, chaque partage affiche ses transferts terminés et le nombre d’IP distinctes observées. Le détail contient le fichier ou le ZIP, la date, l’adresse IP visible et le mode de transfert. **Historique** permet de retrouver les téléchargements après fermeture ou expiration.

Une IP peut être partagée par plusieurs personnes ou changer. Les pages ouvertes, les aperçus locaux et les transferts incomplets ne sont pas comptabilisés. Le journal reste sur le PC dans `%LOCALAPPDATA%\Passload\download-history-tls.jsonl` et garde les 5 000 derniers transferts des 30 derniers jours.

## Recevoir

Ouvrez le lien complet dans un navigateur récent ou collez-le dans **Recevoir** sur un deuxième PC Windows. Les boutons permettent de choisir un fichier ou **Tout télécharger en ZIP**. Le ZIP est construit au fil de l’envoi et les fichiers homonymes conservent des entrées distinctes.

## Réseau public

Le lien ouvre la page statique `transfer.aymen.site` en HTTPS. Les appareils utilisent le service public Piping Server pour échanger des messages de connexion chiffrés. STUN aide WebRTC à découvrir les chemins disponibles. Les fichiers passent directement entre les appareils. Si WebRTC est bloqué, le bouton **Ouvrir le téléchargement HTTPS** ouvre la page servie par le PC expéditeur. [WebRTC](https://www.w3.org/TR/webrtc/) · [Piping Server](https://github.com/nwtgck/piping-server)

Passload demande à la box une redirection TCP temporaire par UPnP ou NAT-PMP et la renouvelle pendant le partage. Sur une connexion où l’opérateur limite les ports disponibles, Passload essaie d’autres plages. Le secours HTTPS utilise l’adresse publique et le port accepté par la box. **Arrêter le partage** libère la redirection après le dernier lien qui l’utilise.

Le port local par défaut est **47831**. Windows peut demander une autorisation de pare-feu pour Passload lors du premier partage public. **Connexion → Réglages avancés** permet aussi de préciser une redirection manuelle, un nom de domaine ou les réglages du pare-feu.

Le mode principal nécessite une adresse IP publique joignable. Un opérateur ou un autre routeur peut bloquer les connexions entrantes. Le runtime Microsoft Edge WebView2 est nécessaire sur le PC qui envoie via WebRTC. Le téléchargement de secours HTTPS reste accessible si le runtime est absent.

L’adresse HTTPS du PC peut utiliser un certificat créé localement. Le navigateur peut alors afficher un avertissement lors du secours HTTPS. **Recevoir** vérifie l’empreinte SHA-256 incluse dans le lien et permet de télécharger dans l’application. Les téléchargements individuels HTTPS acceptent les requêtes de reprise.

Pour que le navigateur reconnaisse aussi le certificat du PC, renseignez votre domaine dans **Connexion → Réglages avancés → Nom de domaine pour le certificat HTTPS**. Son DNS doit pointer vers votre adresse publique et le magasin personnel de votre utilisateur Windows doit contenir un certificat serveur valide avec sa clé privée. Le téléchargement HTTPS utilise ce domaine avec le port accordé par la box.

Vous pouvez obtenir le certificat d’un domaine via une validation DNS, y compris lorsque votre box refuse les ports 80 et 443. Lors d’un renouvellement, installez le nouveau certificat dans le même magasin. Passload le sélectionne la prochaine fois qu’il démarre son serveur de partage. [Validation DNS chez Let’s Encrypt](https://letsencrypt.org/docs/challenge-types/#dns-01-challenge)

## Réseau privé

**Réseau privé** crée un lien HTTPS utilisable sur le même réseau, avec l’adresse locale de l’expéditeur.

L’onglet **Recevoir** ouvre aussi les anciens liens GitHub Pages avec le lecteur intégré à Passload.

## Linux et macOS

Installez le paquet adapté à votre système et à votre architecture. Debian et Ubuntu utilisent le `.deb`, Fedora et les distributions compatibles le `.rpm`, et macOS le `.pkg`. Les paquets Linux enregistrent Passload dans le gestionnaire de paquets et ajoutent une entrée au menu des applications. Chaque plateforme propose aussi une archive portable.

Dans le terminal, lancez `passload document.pdf photo.jpg` pour partager en HTTPS ou ajoutez `--local` pour le réseau local. Depuis une archive extraite, lancez `./passload`. Laissez le terminal ouvert pendant le partage. Pour recevoir avec vérification de l’empreinte, utilisez `passload --receive 'LIEN' destination`. Avec plusieurs fichiers, `destination` reçoit le ZIP. `passload --help` affiche les options. Le terminal utilise la langue du système. Pour la changer, lancez par exemple `PASSLOAD_LANGUAGE=ja passload --help`.

Passload vérifie GitHub au démarrage d’un partage ou d’une réception, puis toutes les heures. L’édition portable vérifie le SHA-256 et se met à jour après le transfert. `passload --check-update` lance une vérification manuelle. Pour les éditions installées, la commande récupère et vérifie le paquet que le gestionnaire du système peut installer.

Les paquets et archives Linux sont attestés par Sigstore. Vérifiez votre téléchargement avec `gh attestation verify fichier -R mertioz/passload`.

## Compiler et vérifier

Sous Windows x64 avec le SDK .NET 10 et le runtime Microsoft Edge WebView2, exécutez les commandes suivantes à la racine du dépôt. Le programme distribué inclut le runtime .NET.

```powershell
dotnet build Passload.slnx -c Release
dotnet run -c Release --project tests/Passload.Checks
dotnet publish src/Passload/Passload.csproj -c Release -r win-x64 -p:PublishProfile=Portable -o artifacts/release
```

Les tests locaux vérifient notamment l’expiration, la révocation, les réponses HTTPS, le ZIP et les redirections simulées. L’option `--network` du programme de vérification teste aussi l’interface réseau et l’ouverture temporaire d’un port avec un fichier de démonstration. Le workflow **Installateurs Passload** construit et vérifie les installations Windows, Linux et macOS. [Confidentialité](PRIVACY.md)

Le workflow manuel **Partage par IP publique** vérifie un lien de démonstration depuis un autre réseau. Il bloque les téléchargements HTTPS pendant le test WebRTC, puis désactive WebRTC pour vérifier le secours sur la même adresse.

## Licence

Le code de Passload est publié sous [licence MIT](LICENSE). Les notices des bibliothèques et de la police sont disponibles dans le [dossier des licences](licenses) et dans **Connexion → Licences du logiciel**.
