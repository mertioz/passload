# Confidentialité de Passload

Passload permet de partager temporairement les fichiers que vous choisissez. L’appareil expéditeur sert les fichiers aux détenteurs du lien. Le destinataire voit le nom et la taille des fichiers ainsi que le titre et la description ajoutés par l’expéditeur. Traitez le lien comme un accès privé aux fichiers pendant la durée du partage.

## Connexion

Cloudflare héberge la page de réception publique. Pour établir une connexion WebRTC, les appareils échangent des messages de mise en relation chiffrés via Piping Server et découvrent leurs adresses réseau via un serveur STUN. Les appareils et ces services peuvent observer les adresses IP nécessaires à la connexion. Les fichiers sont transmis de l’appareil expéditeur à l’appareil destinataire, en WebRTC ou en HTTPS direct.

En ouvrant un ancien lien GitHub Pages dans **Recevoir**, le lecteur intégré peut utiliser l’ancien relais chiffré si l’expéditeur l’avait autorisé. Dans ce cas, le relais transmet les données chiffrées pendant le téléchargement.

## Données conservées sur l’appareil

Sous Windows, Passload enregistre localement les paramètres de connexion et l’historique des transferts terminés. L’historique contient le nom du partage, le nom du fichier, l’heure, le mode de transfert et l’adresse IP du destinataire quand elle est observable. Il est limité aux 5 000 derniers transferts des 30 derniers jours. Vous pouvez retirer l’historique de l’appareil en supprimant les journaux de Passload après avoir fermé l’application.

## Mises à jour

Les versions autonomes consultent GitHub pour les mises à jour. Le Microsoft Store gère les mises à jour des installations effectuées depuis le Store.

## Contact

Les questions sur Passload peuvent être adressées au mainteneur via le [dépôt GitHub du projet](https://github.com/mertioz/passload/issues).
